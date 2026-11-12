// Runs the wasm bundle under Node (same dotnet.js the browser uses) - a fast loop for debugging.
//   node wasm/test/node-run.mjs 'puts 1+1' ['more code' ...]
//   IRW_OPTIONS='{"jit":true}' node wasm/test/node-run.mjs ...
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
const dist = process.env.IRW_DIST || path.join(path.dirname(fileURLToPath(import.meta.url)), '..', 'dist');
const { dotnet } = await import(path.join(dist, '_framework', 'dotnet.js'));
const t0 = performance.now();
globalThis.__ironrubyWrite = (fd, text) => (fd === 2 ? process.stderr : process.stdout).write(text);
const runtime = await (process.env.IRW_ENV ? dotnet.withEnvironmentVariables(JSON.parse(process.env.IRW_ENV)) : dotnet).create();
const exports = await runtime.getAssemblyExports(runtime.getConfig().mainAssemblyName);
const I = exports.IronRuby.Wasm.Interop;
const t1 = performance.now();
const boot = JSON.parse(I.Boot(new Uint8Array(fs.readFileSync(path.join(dist, 'stdlib.zip'))),
  JSON.stringify(Object.assign({ streaming: true }, JSON.parse(process.env.IRW_OPTIONS || '{}')))));
console.error(`[dotnet ${Math.round(t1 - t0)} ms]`, JSON.stringify(boot).slice(0, 4000));
if (!boot.ok) process.exit(1);
let failed = false;
for (const a of process.argv.slice(2)) {
  for (const dll of (a.startsWith('@dll:') ? [a.slice(5)] : [])) {
    console.error(I.LoadAssembly(new Uint8Array(fs.readFileSync(dll)), path.basename(dll)));
  }
  if (a.startsWith('@dll:')) continue;
  const code = a.startsWith('@') ? fs.readFileSync(a.slice(1), 'utf8') : a;
  const r = JSON.parse(I.Run(code, ''));
  if (!r.ok) failed = true;
  delete r.stdout; delete r.stderr;
  console.error('=>', JSON.stringify(r));
}
// like `ruby -e`: code that raised is a failure to the shell
process.exit(failed ? 1 : 0);
