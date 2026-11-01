// PixelStudio CI — browser-side helpers shared by the WebAssembly and WebView2 hosts.
//
// Responsibilities (deliberately small):
//   * present RGBA buffers produced by C# into <canvas> elements (putImageData, no smoothing);
//   * own the viewport transform (integer zoom + pan) and map pointer positions to pixels;
//   * draw view-only overlays (pixel grid, pixel crosshair) on a separate canvas;
//   * forward pointer and keyboard input to .NET in order, batched, one call in flight;
//   * browser file helpers (download / file picker) and the beforeunload guard.
// All document logic (tools, layers, history, compositing, export) lives in C#.

const ZOOM_LEVELS = [1, 2, 3, 4, 6, 8, 12, 16, 24, 32, 48, 64];
const POINTER_DOWN = 0, POINTER_MOVE = 1, POINTER_UP = 2;

const view = {
  width: 32, height: 32, zoom: 8, panX: 0, panY: 0,
  grid: true, crosshair: true, tool: 'Pencil',
  hover: null, drawing: false, panning: null, space: false, last: null,
};

let dotnet = null;
let els = null;
let boundCombos = new Set();
let queue = [];
let pumping = false;
let overlayQueued = false;
let dirty = false;
let pickedBytes = null;

// Input-to-present latency of pointer batches (diagnostics; read by the E2E perf scenario).
const inputStats = { batches: 0, events: 0, totalMs: 0, maxMs: 0 };

// ---------------------------------------------------------------- init

export function initEditor(dotnetRef, combos) {
  dotnet = dotnetRef;
  boundCombos = new Set(combos);
  els = {
    stage: document.getElementById('ps-stage'),
    host: document.getElementById('ps-canvas-host'),
    main: document.getElementById('ps-main'),
    overlay: document.getElementById('ps-overlay'),
    coords: document.getElementById('ps-coords'),
  };

  const stage = els.stage;
  stage.addEventListener('pointerdown', onPointerDown);
  stage.addEventListener('pointermove', onPointerMove);
  stage.addEventListener('pointerup', onPointerUp);
  stage.addEventListener('pointercancel', onPointerUp);
  stage.addEventListener('lostpointercapture', onPointerUp);
  stage.addEventListener('pointerleave', () => { view.hover = null; updateCoords(); requestOverlay(); });
  stage.addEventListener('wheel', onWheel, { passive: false });
  stage.addEventListener('contextmenu', e => e.preventDefault());
  stage.addEventListener('auxclick', e => { if (e.button === 1) e.preventDefault(); });

  window.addEventListener('keydown', onKeyDown);
  window.addEventListener('keyup', e => { if (e.code === 'Space') { view.space = false; updateCursor(); } });
  window.addEventListener('blur', () => { view.space = false; updateCursor(); });
  window.addEventListener('beforeunload', e => {
    if (dirty) { e.preventDefault(); e.returnValue = ''; }
  });

  new ResizeObserver(() => { resizeOverlay(); requestOverlay(); }).observe(stage);
  resizeOverlay();
}

export function setDocument(width, height) {
  view.width = width;
  view.height = height;
  els.main.width = width;
  els.main.height = height;
  const tile = document.getElementById('ps-tile');
  if (tile) { tile.width = width * 3; tile.height = height * 3; }
  fit();
}

export function setOptions(grid, crosshair, tool) {
  view.grid = grid;
  view.crosshair = crosshair;
  view.tool = tool;
  updateCursor();
  requestOverlay();
}

// ---------------------------------------------------------------- presenting pixels

function toImageData(bytes, width, height) {
  // Blazor delivers byte[] as Uint8Array; wrap it without copying when possible.
  const clamped = new Uint8ClampedArray(bytes.buffer, bytes.byteOffset, width * height * 4);
  return new ImageData(clamped, width, height);
}

export function present(bytes, width, height) {
  if (els.main.width !== width || els.main.height !== height) {
    els.main.width = width; els.main.height = height;
  }
  els.main.getContext('2d').putImageData(toImageData(bytes, width, height), 0, 0);
}

/** Incremental update while drawing: only the dirty rectangle is copied to the canvas. */
export function presentRegion(bytes, width, height, x, y, w, h) {
  els.main.getContext('2d').putImageData(toImageData(bytes, width, height), 0, 0, x, y, w, h);
}

/** Tile Preview: the plain composite repeated in a 3x3 grid. */
export function presentTile(bytes, width, height) {
  const tile = document.getElementById('ps-tile');
  if (!tile) return;
  if (tile.width !== width * 3 || tile.height !== height * 3) { tile.width = width * 3; tile.height = height * 3; }
  const ctx = tile.getContext('2d');
  const image = toImageData(bytes, width, height);
  for (let ty = 0; ty < 3; ty++) {
    for (let tx = 0; tx < 3; tx++) ctx.putImageData(image, tx * width, ty * height);
  }
}

export function drawThumbnail(canvasId, bytes, width, height) {
  const canvas = document.getElementById(canvasId);
  if (!canvas) return;
  if (canvas.width !== width || canvas.height !== height) { canvas.width = width; canvas.height = height; }
  canvas.getContext('2d').putImageData(toImageData(bytes, width, height), 0, 0);
}

// ---------------------------------------------------------------- viewport

function stageSize() {
  return { w: els.stage.clientWidth, h: els.stage.clientHeight };
}

function applyTransform() {
  const host = els.host;
  host.style.width = `${view.width * view.zoom}px`;
  host.style.height = `${view.height * view.zoom}px`;
  host.style.transform = `translate(${view.panX}px, ${view.panY}px)`;
  requestOverlay();
}

function setZoomInternal(zoom, anchorX, anchorY) {
  zoom = Math.max(ZOOM_LEVELS[0], Math.min(ZOOM_LEVELS[ZOOM_LEVELS.length - 1], zoom));
  if (zoom === view.zoom) return;
  const { w, h } = stageSize();
  const ax = anchorX ?? w / 2;
  const ay = anchorY ?? h / 2;
  // Keep the document point under the anchor fixed while zooming.
  view.panX = Math.round(ax - (ax - view.panX) * zoom / view.zoom);
  view.panY = Math.round(ay - (ay - view.panY) * zoom / view.zoom);
  view.zoom = zoom;
  applyTransform();
  dotnet?.invokeMethodAsync('OnZoomChanged', zoom);
}

export function zoomStep(direction, anchorX, anchorY) {
  const index = ZOOM_LEVELS.findIndex(z => z >= view.zoom);
  const current = index < 0 ? ZOOM_LEVELS.length - 1 : index;
  const next = ZOOM_LEVELS[Math.max(0, Math.min(ZOOM_LEVELS.length - 1, current + direction))];
  setZoomInternal(next, anchorX, anchorY);
}

export function fit() {
  const { w, h } = stageSize();
  let zoom = ZOOM_LEVELS[0];
  for (const z of ZOOM_LEVELS) {
    if (view.width * z <= w * 0.9 && view.height * z <= h * 0.85) zoom = z;
  }
  view.zoom = zoom;
  view.panX = Math.round((w - view.width * zoom) / 2);
  view.panY = Math.round((h - view.height * zoom) / 2);
  applyTransform();
  dotnet?.invokeMethodAsync('OnZoomChanged', zoom);
}

function onWheel(e) {
  e.preventDefault();
  const rect = els.stage.getBoundingClientRect();
  zoomStep(e.deltaY < 0 ? 1 : -1, e.clientX - rect.left, e.clientY - rect.top);
}

// ---------------------------------------------------------------- pointer input

function toPixel(e) {
  const rect = els.stage.getBoundingClientRect();
  return {
    x: Math.floor((e.clientX - rect.left - view.panX) / view.zoom),
    y: Math.floor((e.clientY - rect.top - view.panY) / view.zoom),
  };
}

function inside(p) {
  return p && p.x >= 0 && p.y >= 0 && p.x < view.width && p.y < view.height;
}

function onPointerDown(e) {
  if (view.drawing || view.panning) return;
  const wantsPan = e.button === 1 || (e.button === 0 && (view.space || view.tool === 'Hand'));
  if (wantsPan) {
    view.panning = { x: e.clientX, y: e.clientY, panX: view.panX, panY: view.panY };
    els.stage.setPointerCapture(e.pointerId);
    updateCursor();
    e.preventDefault();
    return;
  }
  if (e.button !== 0) return;
  els.stage.setPointerCapture(e.pointerId);
  els.stage.focus({ preventScroll: true });
  const p = toPixel(e);
  view.drawing = true;
  view.last = p;
  enqueue(POINTER_DOWN, p.x, p.y);
  e.preventDefault();
}

function onPointerMove(e) {
  if (view.panning) {
    view.panX = view.panning.panX + (e.clientX - view.panning.x);
    view.panY = view.panning.panY + (e.clientY - view.panning.y);
    applyTransform();
    return;
  }

  // Coalesced events keep fast curved strokes faithful; C# joins consecutive points with lines.
  const samples = view.drawing && e.getCoalescedEvents ? e.getCoalescedEvents() : [e];
  for (const sample of samples.length ? samples : [e]) {
    const p = toPixel(sample);
    if (view.drawing && (p.x !== view.last.x || p.y !== view.last.y)) {
      view.last = p;
      enqueue(POINTER_MOVE, p.x, p.y);
    }
  }

  const p = toPixel(e);
  if (!view.hover || view.hover.x !== p.x || view.hover.y !== p.y) {
    view.hover = p;
    updateCoords();
    requestOverlay();
  }
}

function onPointerUp(e) {
  if (view.panning) {
    view.panning = null;
    updateCursor();
    return;
  }
  if (!view.drawing) return;
  view.drawing = false;
  const p = view.last ?? toPixel(e);
  enqueue(POINTER_UP, p.x, p.y);
}

function enqueue(type, x, y) {
  queue.push(type, x, y);
  pump();
}

// One .NET call in flight at a time: preserves event order on both hosts (WASM and WebView2)
// and naturally batches events that arrive while C# is busy.
async function pump() {
  if (pumping || !dotnet) return;
  pumping = true;
  try {
    while (queue.length) {
      const batch = queue;
      queue = [];
      const started = performance.now();
      await dotnet.invokeMethodAsync('OnPointerBatch', batch); // returns after C# drew and presented
      const ms = performance.now() - started;
      inputStats.batches++;
      inputStats.events += batch.length / 3;
      inputStats.totalMs += ms;
      inputStats.maxMs = Math.max(inputStats.maxMs, ms);
    }
  } catch (err) {
    console.error('PixelStudio: pointer dispatch failed', err);
  } finally {
    pumping = false;
  }
}

function updateCoords() {
  if (!els.coords) return;
  els.coords.textContent = inside(view.hover) ? `X ${view.hover.x}  Y ${view.hover.y}` : '—';
}

function updateCursor() {
  const panning = view.panning || view.space || view.tool === 'Hand';
  els.stage.style.cursor = view.panning ? 'grabbing' : panning ? 'grab' : 'crosshair';
}

// ---------------------------------------------------------------- overlay (grid + crosshair)

function resizeOverlay() {
  const dpr = window.devicePixelRatio || 1;
  const { w, h } = stageSize();
  els.overlay.width = Math.max(1, Math.round(w * dpr));
  els.overlay.height = Math.max(1, Math.round(h * dpr));
  els.overlay.style.width = `${w}px`;
  els.overlay.style.height = `${h}px`;
}

function requestOverlay() {
  if (overlayQueued) return;
  overlayQueued = true;
  requestAnimationFrame(() => { overlayQueued = false; drawOverlay(); });
}

function drawOverlay() {
  const canvas = els.overlay;
  const ctx = canvas.getContext('2d');
  const dpr = window.devicePixelRatio || 1;
  ctx.setTransform(1, 0, 0, 1, 0, 0);
  ctx.clearRect(0, 0, canvas.width, canvas.height);

  const z = view.zoom * dpr;
  const ox = view.panX * dpr;
  const oy = view.panY * dpr;
  const W = view.width, H = view.height;
  const snap = v => Math.round(v) + 0.5; // crisp 1-device-pixel lines

  // Sprite bounds.
  ctx.lineWidth = 1;
  ctx.strokeStyle = 'rgba(255,255,255,0.35)';
  ctx.strokeRect(snap(ox - 1), snap(oy - 1), Math.round(W * z) + 1, Math.round(H * z) + 1);

  if (view.grid && view.zoom >= 4) {
    const x0 = Math.max(0, Math.floor(-ox / z)), x1 = Math.min(W, Math.ceil((canvas.width - ox) / z));
    const y0 = Math.max(0, Math.floor(-oy / z)), y1 = Math.min(H, Math.ceil((canvas.height - oy) / z));
    for (const major of [false, true]) {
      ctx.beginPath();
      ctx.strokeStyle = major ? 'rgba(255,255,255,0.32)' : 'rgba(255,255,255,0.16)';
      for (let x = x0; x <= x1; x++) {
        if ((x % 8 === 0) !== major) continue;
        const sx = snap(ox + x * z);
        ctx.moveTo(sx, oy + y0 * z); ctx.lineTo(sx, oy + y1 * z);
      }
      for (let y = y0; y <= y1; y++) {
        if ((y % 8 === 0) !== major) continue;
        const sy = snap(oy + y * z);
        ctx.moveTo(ox + x0 * z, sy); ctx.lineTo(ox + x1 * z, sy);
      }
      ctx.stroke();
    }
  }

  const p = view.hover;
  if (view.crosshair && inside(p) && !view.panning && !view.space && view.tool !== 'Hand') {
    // Exact cell of the pixel under the pointer, in device pixels.
    const left = Math.round(ox + p.x * z), top = Math.round(oy + p.y * z);
    const right = Math.round(ox + (p.x + 1) * z), bottom = Math.round(oy + (p.y + 1) * z);
    const cx = snap((left + right) / 2 - 0.5), cy = snap((top + bottom) / 2 - 0.5);
    const gap = Math.max(3 * dpr, 2);

    // Guide lines along the pixel's row and column, stopping short of the cell itself.
    ctx.beginPath();
    ctx.strokeStyle = 'rgba(77,163,255,0.55)';
    ctx.moveTo(Math.round(ox), cy); ctx.lineTo(left - gap, cy);
    ctx.moveTo(right + gap, cy); ctx.lineTo(Math.round(ox + W * z), cy);
    ctx.moveTo(cx, Math.round(oy)); ctx.lineTo(cx, top - gap);
    ctx.moveTo(cx, bottom + gap); ctx.lineTo(cx, Math.round(oy + H * z));
    ctx.stroke();

    // Two-tone outline so the cell is visible on any color.
    const pad = view.zoom < 4 ? 1 : 0;
    ctx.strokeStyle = 'rgba(0,0,0,0.9)';
    ctx.strokeRect(left - pad - 0.5, top - pad - 0.5, right - left + 2 * pad + 1, bottom - top + 2 * pad + 1);
    ctx.strokeStyle = 'rgba(255,255,255,0.95)';
    ctx.strokeRect(left - pad + 0.5, top - pad + 0.5, right - left + 2 * pad - 1, bottom - top + 2 * pad - 1);
  }
}

// ---------------------------------------------------------------- keyboard

function isTextInput(el) {
  if (!el) return false;
  if (el.isContentEditable) return true;
  const tag = el.tagName;
  if (tag === 'TEXTAREA' || tag === 'SELECT') return true;
  if (tag !== 'INPUT') return false;
  const nonText = ['button', 'checkbox', 'radio', 'range', 'color', 'submit', 'reset', 'file', 'image'];
  return !nonText.includes((el.type || 'text').toLowerCase());
}

function comboOf(e) {
  // KeyboardEvent.code is layout-independent: "B" works on a Russian (ЙЦУКЕН) layout too.
  let combo = '';
  if (e.ctrlKey || e.metaKey) combo += 'Ctrl+';
  if (e.shiftKey) combo += 'Shift+';
  if (e.altKey) combo += 'Alt+';
  return combo + e.code;
}

function onKeyDown(e) {
  if (e.code === 'Escape') {
    dotnet?.invokeMethodAsync('OnShortcut', 'Escape');
    return;
  }
  // Shortcuts never fire while the user types in a text field.
  if (isTextInput(e.target)) return;

  if (e.code === 'Space') {
    view.space = true;
    updateCursor();
    e.preventDefault();
    return;
  }

  const combo = comboOf(e);
  if (!boundCombos.has(combo)) return;
  e.preventDefault();
  if (e.repeat && !combo.endsWith('KeyZ') && !combo.endsWith('KeyY') && combo !== 'Comma' && combo !== 'Period') return;
  dotnet?.invokeMethodAsync('OnShortcut', combo);
}

export function getInputStats(reset) {
  const result = { ...inputStats, pending: queue.length + (pumping ? 1 : 0) };
  if (reset) Object.assign(inputStats, { batches: 0, events: 0, totalMs: 0, maxMs: 0 });
  return result;
}

// ---------------------------------------------------------------- shell / files (browser host)

export function setDocumentState(title, isDirty) {
  document.title = title;
  dirty = isDirty;
}

export function downloadFile(fileName, mimeType, bytes) {
  const blob = new Blob([bytes], { type: mimeType });
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = fileName;
  a.style.display = 'none';
  document.body.appendChild(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 30000);
}

/** Opens the native file picker; resolves to the file name, or null when cancelled. */
export function pickFile(accept) {
  return new Promise(resolve => {
    const input = document.createElement('input');
    input.type = 'file';
    input.accept = accept;
    input.style.display = 'none';
    let settled = false;
    const done = value => {
      if (settled) return;
      settled = true;
      input.remove();
      resolve(value);
    };
    input.addEventListener('change', async () => {
      const file = input.files && input.files[0];
      if (!file) { done(null); return; }
      pickedBytes = new Uint8Array(await file.arrayBuffer());
      done(file.name);
    });
    input.addEventListener('cancel', () => done(null));
    document.body.appendChild(input);
    input.click();
  });
}

export function takePickedBytes() {
  const bytes = pickedBytes;
  pickedBytes = null;
  return bytes ?? new Uint8Array(0);
}

export function focusElement(id) {
  const el = document.getElementById(id);
  if (el) { el.focus(); if (el.select) el.select(); }
}
