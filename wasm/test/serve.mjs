// Minimal static server for wasm/dist (and the test pages): node test/serve.mjs [port] [root]
import http from 'node:http';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const TYPES = { '.html': 'text/html; charset=utf-8', '.js': 'text/javascript', '.mjs': 'text/javascript',
  '.wasm': 'application/wasm', '.json': 'application/json', '.zip': 'application/zip',
  '.dll': 'application/octet-stream', '.dat': 'application/octet-stream', '.css': 'text/css' };

// root: a directory, or { '/prefix/': dir, ..., '/': dir } (longest prefix wins)
export function serve(root, port = 0) {
  const mounts = typeof root === 'string' ? { '/': root } : root;
  const prefixes = Object.keys(mounts).sort((a, b) => b.length - a.length);
  const server = http.createServer((req, res) => {
    // a path that does not decode (`/%`) is that request's error, not the server's: uncaught, it ends the process
    let p;
    try {
      p = decodeURIComponent(new URL(req.url, 'http://x').pathname);
    } catch {
      res.writeHead(400); res.end('bad request'); return;
    }
    if (p.endsWith('/')) p += 'index.html';
    const prefix = prefixes.find(x => p.startsWith(x));
    const dir = path.resolve(mounts[prefix]);
    const file = path.join(dir, path.normalize(p.slice(prefix.length - 1)).replace(/^(\.\.[/\\])+/, ''));
    if (!file.startsWith(dir) || !fs.existsSync(file) || fs.statSync(file).isDirectory()) {
      res.writeHead(404); res.end('not found'); return;
    }
    const headers = { 'Content-Type': TYPES[path.extname(file)] || 'application/octet-stream', 'Cache-Control': 'no-cache' };
    const accept = req.headers['accept-encoding'] || '';
    if (accept.includes('br') && fs.existsSync(file + '.br')) {
      headers['Content-Encoding'] = 'br';
      res.writeHead(200, headers); fs.createReadStream(file + '.br').pipe(res); return;
    }
    res.writeHead(200, headers); fs.createReadStream(file).pipe(res);
  });
  return new Promise(resolve => server.listen(port, '127.0.0.1', () => resolve(server)));
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  const root = path.resolve(process.argv[3] || path.join(path.dirname(fileURLToPath(import.meta.url)), '..', 'dist'));
  const s = await serve(root, Number(process.argv[2] || 8790));
  console.log(`serving ${root} on http://127.0.0.1:${s.address().port}/`);
}
