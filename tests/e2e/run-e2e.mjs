// End-to-end test of the main scenario in a real browser (Chromium via Playwright):
//   create project → draw → layers & frames → playback → save → reopen → export PNG + JSON.
//
// Usage: node tests/e2e/run-e2e.mjs <published wwwroot> [--out <dir>] [--base /Repo/] [--perf] [--headed]
// The published app is served under a sub-path (default /PixelStudio-CI/) like GitHub Pages does.
import assert from 'node:assert/strict';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { createRequire } from 'node:module';
import { join, resolve } from 'node:path';
import { startServer } from './static-server.mjs';

async function loadPlaywright() {
  try {
    return await import('playwright');
  } catch {
    // Fallback: globally installed Playwright (e.g. preinstalled in a dev container).
    const require = createRequire(import.meta.url);
    const { execSync } = await import('node:child_process');
    const globalRoot = execSync('npm root -g').toString().trim();
    return require(join(globalRoot, 'playwright'));
  }
}

const args = process.argv.slice(2);
const root = resolve(args.find(a => !a.startsWith('--')) ?? 'publish/wwwroot');
const outDir = resolve(args.includes('--out') ? args[args.indexOf('--out') + 1] : 'tests/e2e/output');
const runPerf = args.includes('--perf');
const headed = args.includes('--headed');
const PORT = 5055;
// Base path the site is served under (GitHub Pages project sites use /<repository>/).
const BASE = args.includes('--base') ? args[args.indexOf('--base') + 1] : '/PixelStudio-CI/';

await mkdir(outDir, { recursive: true });
const { chromium } = await loadPlaywright();
const server = await startServer(root, PORT, BASE);
const browser = await chromium.launch({ headless: !headed });
const context = await browser.newContext({ viewport: { width: 1440, height: 900 }, acceptDownloads: true });
const page = await context.newPage();
const problems = [];
page.on('pageerror', e => problems.push(`pageerror: ${e.message}`));
page.on('console', m => { if (m.type() === 'error') problems.push(`console: ${m.text()}`); });

let step = 0;
async function check(name, fn) {
  step++;
  const started = Date.now();
  try {
    await fn();
    console.log(`  ✔ ${String(step).padStart(2)} ${name} (${Date.now() - started} ms)`);
  } catch (err) {
    console.log(`  ✘ ${String(step).padStart(2)} ${name}`);
    await page.screenshot({ path: join(outDir, `failure-${step}.png`) }).catch(() => {});
    throw err;
  }
}

const byTest = id => page.locator(`[data-testid="${id}"]`);

/** Screen position of the centre of document pixel (x, y). */
async function pixelPoint(x, y) {
  const box = await page.locator('#ps-canvas-host').boundingBox();
  const width = await page.evaluate(() => document.getElementById('ps-main').width);
  const zoom = box.width / width;
  return { x: box.x + (x + 0.5) * zoom, y: box.y + (y + 0.5) * zoom };
}

async function stroke(points) {
  const first = await pixelPoint(...points[0]);
  await page.mouse.move(first.x, first.y);
  await page.mouse.down();
  for (const p of points.slice(1)) {
    const q = await pixelPoint(...p);
    await page.mouse.move(q.x, q.y, { steps: 1 }); // one jump: simulates a very fast pointer
  }
  await page.mouse.up();
  await settle();
}

async function click(x, y) {
  const p = await pixelPoint(x, y);
  await page.mouse.click(p.x, p.y);
  await settle();
}

/** Lets pending .NET → JS presents finish. */
async function settle() {
  await page.waitForTimeout(120);
}

/** Pixels of the main canvas (what the editor shows: composite + onion skin). */
async function mainPixels() {
  return page.evaluate(() => {
    const c = document.getElementById('ps-main');
    return { w: c.width, h: c.height, data: Array.from(c.getContext('2d').getImageData(0, 0, c.width, c.height).data) };
  });
}

const px = (img, x, y) => img.data.slice((y * img.w + x) * 4, (y * img.w + x) * 4 + 4);

async function download(trigger) {
  const [file] = await Promise.all([page.waitForEvent('download'), trigger()]);
  const path = join(outDir, file.suggestedFilename());
  await file.saveAs(path);
  return path;
}

async function downloads(count, trigger) {
  const files = [];
  const done = new Promise(resolveAll => {
    page.on('download', async d => {
      const path = join(outDir, d.suggestedFilename());
      await d.saveAs(path);
      files.push(path);
      if (files.length === count) resolveAll();
    });
  });
  await trigger();
  await Promise.race([done, page.waitForTimeout(15000)]);
  page.removeAllListeners('download');
  assert.equal(files.length, count, `expected ${count} downloads`);
  return files;
}

async function statusText() {
  return (await byTest('status').textContent({ timeout: 5000 }))?.trim() ?? '';
}

console.log(`E2E: ${root} served at http://127.0.0.1:${PORT}${BASE}`);
try {
  await check('app loads under the GitHub Pages base path', async () => {
    const started = Date.now();
    await page.goto(`http://127.0.0.1:${PORT}${BASE}`);
    await byTest('editor').waitFor({ timeout: 60000 });
    console.log(`       startup: ${Date.now() - started} ms`);
    await settle();
  });

  await check('new 16×16 project from preset', async () => {
    await byTest('btn-new').click();
    await byTest('dialog-new').waitFor();
    await byTest('preset-16').click();
    await byTest('new-create').click();
    await byTest('dialog-new').waitFor({ state: 'detached' });
    assert.match(await byTest('doc-name').textContent(), /16×16/);
    const img = await mainPixels();
    assert.equal(img.w, 16);
    assert.ok(img.data.every(v => v === 0), 'new canvas is transparent');
  });

  await check('fast pencil stroke is continuous and undoable as one step', async () => {
    await byTest('palette-2').click(); // #B13E53
    await stroke([[1, 1], [14, 6]]);
    const img = await mainPixels();
    let painted = 0;
    for (let i = 0; i < img.data.length; i += 4) if (img.data[i + 3] === 255) painted++;
    assert.equal(painted, 14, 'Bresenham line from (1,1) to (14,6) has 14 pixels');
    assert.deepEqual(px(img, 1, 1), [0xB1, 0x3E, 0x53, 255]);
    assert.deepEqual(px(img, 14, 6), [0xB1, 0x3E, 0x53, 255]);
    assert.ok(await byTest('dirty-indicator').isVisible(), 'document is marked dirty');

    await page.keyboard.press('Control+z');
    await settle();
    assert.ok((await mainPixels()).data.every(v => v === 0), 'one Ctrl+Z removes the whole stroke');
    await page.keyboard.press('Control+y');
    await settle();
    assert.deepEqual(px(await mainPixels(), 14, 6), [0xB1, 0x3E, 0x53, 255]);
  });

  await check('hotkeys select tools; fill and eyedropper work', async () => {
    await page.keyboard.press('g');
    assert.equal(await byTest('tool-fill').getAttribute('aria-pressed'), 'true');
    await byTest('palette-10').click(); // #41A6F6
    await click(0, 15);
    let img = await mainPixels();
    assert.deepEqual(px(img, 0, 15), [0x41, 0xA6, 0xF6, 255]);
    assert.deepEqual(px(img, 15, 0), [0x41, 0xA6, 0xF6, 255], 'fill reaches around the line');
    assert.deepEqual(px(img, 1, 1), [0xB1, 0x3E, 0x53, 255], 'line is a border, not filled');

    await page.keyboard.press('i');
    await click(5, 3); // on the line
    assert.equal(await byTest('color-hex').inputValue(), '#B13E53FF');
    await page.keyboard.press('b');
    assert.equal(await byTest('tool-pencil').getAttribute('aria-pressed'), 'true');
  });

  await check('layers: add, draw, rename (typing does not trigger hotkeys), blend, opacity', async () => {
    await byTest('layer-add').click();
    assert.equal(await byTest('layer-row').count(), 2);
    await byTest('color-hex').fill('#808080FF');
    await byTest('color-hex').press('Enter');
    await stroke([[3, 10], [12, 10]]);
    let img = await mainPixels();
    assert.deepEqual(px(img, 5, 10), [128, 128, 128, 255]);

    await byTest('layer-name').first().dblclick();
    const input = byTest('layer-rename-input');
    await input.waitFor();
    await input.fill('');
    await input.type('Тень bigex'); // contains B, I, G, E, X hotkey letters
    await input.press('Enter');
    assert.equal(await byTest('tool-pencil').getAttribute('aria-pressed'), 'true', 'typing must not switch tools');
    assert.equal((await byTest('layer-name').first().textContent()).trim(), 'Тень bigex');

    await byTest('layer-blend').selectOption('Multiply');
    await settle();
    img = await mainPixels();
    // Multiply #808080 over #41A6F6 → round(0x41*128/255) etc.
    assert.deepEqual(px(img, 5, 10), [33, 83, 123, 255]);

    await byTest('layer-opacity').fill('50');
    await settle();
    assert.match(await byTest('layer-row').first().textContent(), /50%/);
  });

  await check('duplicate layer is independent; visibility toggles', async () => {
    await byTest('layer-duplicate').click();
    assert.equal(await byTest('layer-row').count(), 3);
    await byTest('layer-visibility').first().click(); // hide the copy (top row)
    await settle();
    const withoutCopy = await mainPixels();
    await byTest('layer-visibility').first().click();
    await byTest('layer-delete').click(); // delete the copy
    assert.equal(await byTest('layer-row').count(), 2);
    await settle();
    assert.deepEqual((await mainPixels()).data, withoutCopy.data);
  });

  let frame1;
  await check('frames: duplicate, edit copy, durations, FPS', async () => {
    frame1 = await mainPixels();
    await byTest('frame-duplicate').click();
    assert.equal(await byTest('frame-thumb').count(), 2);
    await byTest('palette-5').click(); // #A7F070
    await stroke([[8, 13], [8, 15]]);
    await byTest('duration-default').uncheck();
    await byTest('duration-input').fill('250');
    await byTest('duration-input').press('Enter');
    await byTest('frame-add').click();
    assert.equal(await byTest('frame-thumb').count(), 3);
    await byTest('fps-input').fill('10');
    await byTest('fps-input').press('Enter');
    await settle();

    await byTest('frame-thumb').nth(0).click();
    await settle();
    assert.deepEqual((await mainPixels()).data, frame1.data, 'editing the duplicate did not touch frame 1');
    const titles = await byTest('frame-thumb').evaluateAll(els => els.map(e => e.title));
    assert.match(titles[0], /100 мс/);
    assert.match(titles[1], /250 мс/);
    assert.match(titles[2], /100 мс/);
  });

  await check('onion skin tints neighbours; view-only', async () => {
    await byTest('frame-thumb').nth(2).click(); // empty frame 3; previous = frame 2
    await page.keyboard.press('o');
    await settle();
    let img = await mainPixels();
    const ghost = px(img, 0, 15);
    assert.ok(ghost[3] > 0 && ghost[3] < 255, `ghost is semi-transparent (${ghost})`);
    assert.ok(ghost[0] > ghost[2], `previous frame is tinted red (${ghost})`);
    await page.screenshot({ path: join(outDir, 'onion-skin.png') });
    await page.keyboard.press('o');
    await settle();
    img = await mainPixels();
    assert.ok(img.data.every(v => v === 0), 'without onion skin the empty frame is empty');
  });

  await check('playback plays and pauses', async () => {
    await byTest('frame-thumb').nth(0).click();
    await page.keyboard.press('p');
    assert.equal(await byTest('btn-play').getAttribute('aria-pressed'), 'true');
    const seen = new Set();
    for (let i = 0; i < 12; i++) {
      await page.waitForTimeout(50);
      seen.add(await page.locator('.ps-frame.is-active').getAttribute('data-frame-id'));
    }
    await page.keyboard.press('p');
    assert.equal(await byTest('btn-play').getAttribute('aria-pressed'), 'false');
    assert.ok(seen.size >= 2, `frames advanced during playback (${[...seen]})`);
  });

  await check('tile preview repeats the composite 3×3', async () => {
    await byTest('frame-thumb').nth(0).click();
    await byTest('toggle-tile').check();
    await settle();
    const tile = await page.evaluate(() => {
      const c = document.getElementById('ps-tile');
      return { w: c.width, h: c.height, data: Array.from(c.getContext('2d').getImageData(0, 0, c.width, c.height).data) };
    });
    assert.equal(tile.w, 48);
    const main = await mainPixels();
    for (const [tx, ty] of [[0, 0], [1, 1], [2, 2], [2, 0]]) {
      assert.deepEqual(px(tile, tx * 16 + 5, ty * 16 + 10), px(main, 5, 10));
    }
    await page.screenshot({ path: join(outDir, 'editor.png') });
  });

  await check('viewport: wheel zoom, hand-tool pan, pixelated scaling, pixel crosshair', async () => {
    const stage = await byTest('stage').boundingBox();
    const zoomBefore = await byTest('zoom-label').textContent();
    await page.mouse.move(stage.x + stage.width / 2, stage.y + stage.height / 2);
    await page.mouse.wheel(0, -200);
    await settle();
    assert.notEqual(await byTest('zoom-label').textContent(), zoomBefore, 'wheel changes zoom');
    assert.equal(await page.evaluate(() => getComputedStyle(document.getElementById('ps-main')).imageRendering), 'pixelated');

    const before = await page.locator('#ps-canvas-host').boundingBox();
    await page.keyboard.press('h');
    await page.mouse.move(stage.x + 100, stage.y + 100);
    await page.mouse.down();
    await page.mouse.move(stage.x + 160, stage.y + 130, { steps: 4 });
    await page.mouse.up();
    const after = await page.locator('#ps-canvas-host').boundingBox();
    assert.deepEqual([Math.round(after.x - before.x), Math.round(after.y - before.y)], [60, 30], 'hand tool pans');
    await page.keyboard.press('b');

    // Crosshair: bright two-tone outline around the hovered pixel (grid/guides are dimmer).
    const brightNear = async (x, y) => {
      const p = await pixelPoint(x, y);
      await page.mouse.move(p.x, p.y);
      await page.waitForTimeout(120);
      return page.evaluate(({ px, py }) => {
        const c = document.getElementById('ps-overlay');
        const r = c.getBoundingClientRect();
        const dpr = c.width / r.width;
        const d = c.getContext('2d').getImageData(Math.round((px - r.left) * dpr) - 40, Math.round((py - r.top) * dpr) - 40, 80, 80).data;
        let n = 0;
        for (let i = 3; i < d.length; i += 4) if (d[i] > 200) n++;
        return n;
      }, { px: p.x, py: p.y });
    };
    assert.ok(await brightNear(3, 4) > 0, 'crosshair outline is drawn at the hovered pixel');
    assert.match(await page.locator('#ps-coords').textContent(), /X 3\s+Y 4/);
    await page.keyboard.press('x');
    assert.equal(await brightNear(5, 5), 0, 'crosshair can be switched off');
    await page.keyboard.press('x');
    await page.keyboard.press('0'); // fit back
    await settle();
  });

  let projectPath;
  await check('save project (download) clears the dirty flag', async () => {
    projectPath = await download(() => page.keyboard.press('Control+s'));
    assert.match(projectPath, /\.pxstudio$/);
    await settle();
    assert.equal(await byTest('dirty-indicator').count(), 0);
    const json = JSON.parse(await readFile(projectPath, 'utf8'));
    assert.equal(json.format, 'PixelStudioProject');
    assert.equal(json.formatVersion, 1);
    assert.equal(json.layers.length, 2);
    assert.equal(json.frames.length, 3);
    assert.equal(json.frames[1].durationMs, 250);
  });

  let sheetPng, sheetJson, framePng;
  await check('export frame PNG and sprite sheet PNG + JSON', async () => {
    await page.keyboard.press('Control+Shift+E');
    await byTest('dialog-export').waitFor();
    framePng = await download(() => byTest('export-frame').click());
    await page.keyboard.press('Control+Shift+E');
    await byTest('dialog-export').waitFor();
    await byTest('export-name').fill('hero');
    await byTest('export-columns').fill('2');
    await byTest('export-spacing').fill('1');
    assert.match(await byTest('export-summary').textContent(), /атлас 33×33 px, 2×2/);
    const files = await downloads(2, () => byTest('export-sheet').click());
    sheetPng = files.find(f => f.endsWith('hero.png'));
    sheetJson = files.find(f => f.endsWith('hero.json'));
    assert.ok(sheetPng && sheetJson, files.join(', '));
  });

  await check('sprite sheet PNG and JSON agree with the editor frames', async () => {
    const meta = JSON.parse(await readFile(sheetJson, 'utf8'));
    assert.equal(meta.image, 'hero.png');
    assert.deepEqual(meta.atlas, { width: 33, height: 33 });
    assert.deepEqual(meta.frames.map(f => [f.x, f.y, f.width, f.height, f.durationMs]),
      [[0, 0, 16, 16, 100], [17, 0, 16, 16, 250], [0, 17, 16, 16, 100]]);
    assert.deepEqual(meta.playback.order, [0, 1, 2]);
    assert.equal(meta.playback.totalDurationMs, 450);

    // Decode the PNG with the browser's own decoder (independent of our C# encoder).
    const b64 = (await readFile(sheetPng)).toString('base64');
    const atlas = await page.evaluate(async b64 => {
      const bytes = Uint8Array.from(atob(b64), c => c.charCodeAt(0));
      const bmp = await createImageBitmap(new Blob([bytes], { type: 'image/png' }), { premultiplyAlpha: 'none', colorSpaceConversion: 'none' });
      const c = new OffscreenCanvas(bmp.width, bmp.height);
      const ctx = c.getContext('2d');
      ctx.drawImage(bmp, 0, 0);
      return { w: bmp.width, h: bmp.height, data: Array.from(ctx.getImageData(0, 0, bmp.width, bmp.height).data) };
    }, b64);
    assert.equal(atlas.w, 33);
    for (const f of meta.frames) {
      await byTest('frame-thumb').nth(f.index).click();
      await settle();
      const editor = await mainPixels();
      for (let y = 0; y < f.height; y++) {
        for (let x = 0; x < f.width; x++) {
          assert.deepEqual(px(atlas, f.x + x, f.y + y), px(editor, x, y), `frame ${f.index} pixel ${x},${y}`);
        }
      }
    }
    // Spacing column/row stays transparent: no grid or crosshair leaks into exports.
    for (let i = 0; i < 33; i++) {
      assert.equal(px(atlas, 16, i)[3], 0);
      assert.equal(px(atlas, i, 16)[3], 0);
    }
    assert.equal(px(atlas, 20, 20)[3], 0, 'unused 4th cell is transparent');
  });

  await check('reload and reopen the project: document restored without loss', async () => {
    await byTest('frame-thumb').nth(1).click();
    await settle();
    const before = await mainPixels();
    await page.reload();
    await byTest('editor').waitFor({ timeout: 60000 });
    const [chooser] = await Promise.all([page.waitForEvent('filechooser'), byTest('btn-open').click()]);
    await chooser.setFiles(projectPath);
    await page.waitForFunction(() => document.querySelectorAll('[data-testid=frame-thumb]').length === 3);
    await settle();
    assert.match(await statusText(), /Открыт проект/);
    assert.match(await byTest('doc-name').textContent(), /16×16/);
    assert.equal((await byTest('layer-name').first().textContent()).trim(), 'Тень bigex');
    assert.equal(await byTest('layer-blend').inputValue(), 'Multiply');
    assert.equal(await byTest('fps-input').inputValue(), '10');
    await byTest('frame-thumb').nth(1).click();
    await settle();
    assert.deepEqual((await mainPixels()).data, before.data);
    assert.equal(await byTest('duration-input').inputValue(), '250');
    assert.equal(await byTest('btn-undo').isDisabled(), true, 'history starts clean after open');
  });

  await check('corrupted and future-version files give clear errors', async () => {
    const junk = join(outDir, 'broken.pxstudio');
    await writeFile(junk, '{"format":"PixelStudioProject","formatVersion":1,"width":16');
    let [chooser] = await Promise.all([page.waitForEvent('filechooser'), byTest('btn-open').click()]);
    await chooser.setFiles(junk);
    await page.waitForFunction(() => /Не удалось открыть/.test(document.querySelector('[data-testid=status]')?.textContent ?? ''));
    assert.match(await statusText(), /некорректный JSON/);

    const future = join(outDir, 'future.pxstudio');
    await writeFile(future, '{"format":"PixelStudioProject","formatVersion":7}');
    [chooser] = await Promise.all([page.waitForEvent('filechooser'), byTest('btn-open').click()]);
    await chooser.setFiles(future);
    await page.waitForFunction(() => /версии 7/.test(document.querySelector('[data-testid=status]')?.textContent ?? ''));
    assert.equal(await byTest('frame-thumb').count(), 3, 'the open document is untouched');
  });

  await check('unsaved changes: confirm dialog and beforeunload guard', async () => {
    await stroke([[2, 2], [3, 3]]);
    assert.ok(await byTest('dirty-indicator').isVisible());
    await byTest('btn-new').click();
    await byTest('dialog-confirm').waitFor();
    await byTest('confirm-cancel').click();
    await byTest('dialog-confirm').waitFor({ state: 'detached' });
    assert.equal(await byTest('frame-thumb').count(), 3, 'cancel keeps the document');

    let sawPrompt = false;
    page.once('dialog', async dialog => {
      sawPrompt = dialog.type() === 'beforeunload';
      await dialog.dismiss();
    });
    await page.close({ runBeforeUnload: true });
    await new Promise(r => setTimeout(r, 500));
    assert.ok(sawPrompt, 'browser asked before leaving with unsaved changes');
  });

  if (runPerf) {
    await check('drawing latency (pointer batch → C# → canvas) on 64×64 and 256×256 with onion skin', async () => {
      const perfPage = await context.newPage();
      await perfPage.goto(`http://127.0.0.1:${PORT}${BASE}`);
      await perfPage.locator('[data-testid=editor]').waitFor({ timeout: 60000 });
      const lines = [];
      for (const size of [64, 256]) {
        await perfPage.locator('[data-testid=btn-new]').click();
        const discard = perfPage.locator('[data-testid=confirm-discard]');
        if (await discard.isVisible().catch(() => false)) await discard.click();
        await perfPage.locator('[data-testid=new-width]').fill(String(size));
        await perfPage.locator('[data-testid=new-height]').fill(String(size));
        await perfPage.locator('[data-testid=new-height]').press('Tab');
        await perfPage.locator('[data-testid=new-create]').click();
        await perfPage.locator('[data-testid=frame-duplicate]').click();
        await perfPage.locator('[data-testid=layer-add]').click();
        await perfPage.locator('[data-testid=toggle-onion]').check();
        await perfPage.waitForTimeout(300);
        const box = await perfPage.locator('#ps-canvas-host').boundingBox();
        await perfPage.evaluate(async () => (await import('./_content/PixelStudio.UI/pixelstudio.js')).getInputStats(true));
        await perfPage.mouse.move(box.x + 2, box.y + 2);
        await perfPage.mouse.down();
        for (let i = 1; i <= 120; i++) {
          const t = i / 120;
          await perfPage.mouse.move(box.x + 2 + t * (box.width - 4), box.y + box.height / 2 + Math.sin(t * 12) * box.height * 0.4);
        }
        await perfPage.mouse.up();
        await perfPage.waitForFunction(async () => (await import('./_content/PixelStudio.UI/pixelstudio.js')).getInputStats(false).pending === 0);
        const s = await perfPage.evaluate(async () => (await import('./_content/PixelStudio.UI/pixelstudio.js')).getInputStats(true));
        lines.push(`| ${size}×${size}, 2 layers, onion skin on | ${s.events} | ${s.batches} | ${(s.totalMs / s.batches).toFixed(2)} | ${s.maxMs.toFixed(2)} |`);
      }
      const table = ['| Canvas | Pointer events | Batches | Mean batch, ms | Max batch, ms |', '|---|---:|---:|---:|---:|', ...lines].join('\n');
      await writeFile(join(outDir, 'perf-input.md'), table + '\n');
      console.log(table.split('\n').map(l => `       ${l}`).join('\n'));
      await perfPage.close();
    });

    await check('performance probe on .NET WebAssembly', async () => {
      const perfPage = await context.newPage();
      await perfPage.goto(`http://127.0.0.1:${PORT}${BASE}`);
      await perfPage.locator('[data-testid=editor]').waitFor({ timeout: 60000 });
      const table = await perfPage.evaluate(() => DotNet.invokeMethodAsync('PixelStudio.Web', 'RunPerfProbe'));
      await writeFile(join(outDir, 'perf-wasm.md'), table + '\n');
      console.log(table.split('\n').map(l => `       ${l}`).join('\n'));
      await perfPage.close();
    });
  }

  assert.deepEqual(problems, [], 'no page errors');
  console.log(`\nE2E passed: ${step} checks. Artifacts in ${outDir}`);
} finally {
  await browser.close();
  server.close();
}
