// Minimal static file server for the published WebAssembly app (no dependencies).
// Serves <root> under <basePath>, mimicking GitHub Pages project sites (/<repo>/).
// Usage: node static-server.mjs <root> [port=5055] [basePath=/PixelStudio-CI/]
import { createServer } from 'node:http';
import { readFile, stat } from 'node:fs/promises';
import { extname, join, normalize, resolve } from 'node:path';

const MIME = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.mjs': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.json': 'application/json; charset=utf-8',
  '.wasm': 'application/wasm',
  '.svg': 'image/svg+xml',
  '.png': 'image/png',
  '.ico': 'image/x-icon',
  '.dat': 'application/octet-stream',
  '.pdb': 'application/octet-stream',
};

export function startServer(root, port = 5055, basePath = '/PixelStudio-CI/') {
  const rootDir = resolve(root);
  const server = createServer(async (req, res) => {
    try {
      const url = new URL(req.url, 'http://localhost');
      let path = decodeURIComponent(url.pathname);
      if (!path.startsWith(basePath)) {
        res.writeHead(404).end('Not under base path');
        return;
      }
      path = path.slice(basePath.length) || 'index.html';
      const file = normalize(join(rootDir, path));
      if (!file.startsWith(rootDir)) {
        res.writeHead(403).end();
        return;
      }
      const info = await stat(file).catch(() => null);
      const target = info?.isDirectory() ? join(file, 'index.html') : file;
      let body = await readFile(target);
      if (target.endsWith('index.html')) {
        // Same rewrite the Pages workflow performs (no-op if already rewritten).
        body = Buffer.from(body.toString('utf8').replace('<base href="/" />', `<base href="${basePath}" />`));
      }
      res.writeHead(200, { 'Content-Type': MIME[extname(target)] ?? 'application/octet-stream', 'Cache-Control': 'no-store' });
      res.end(body);
    } catch {
      res.writeHead(404).end('Not found');
    }
  });
  return new Promise(ok => server.listen(port, '127.0.0.1', () => ok(server)));
}

if (process.argv[1] && import.meta.url === new URL(`file://${process.argv[1]}`).href) {
  const [root, port, base] = process.argv.slice(2);
  const server = await startServer(root ?? '.', Number(port ?? 5055), base ?? '/PixelStudio-CI/');
  console.log(`Serving ${resolve(root ?? '.')} at http://127.0.0.1:${server.address().port}${base ?? '/PixelStudio-CI/'}`);
}
