// IronRuby in the browser: a thin wrapper over the [JSExport]s in IronRuby.Wasm/Interop.cs.
//
//   import { bootIronRuby } from './ironruby.js';
//   const ir = await bootIronRuby({ onOutput: (fd, text) => ... });
//   ir.run('x = 1 + 1')      // => { ok: true, result: "2", stdout: "", stderr: "", ms }
//   ir.run('puts x')         // locals persist between runs
//   await ir.loadAssemblyFromUrl('Markdig.dll');  then  ir.run("require 'Markdig'; ...")
//
// Works on the main thread or in a Web Worker (runs block the thread they run on, so a page that
// must stay responsive should host it in a worker).
export async function bootIronRuby(opts = {}) {
  const base = new URL(opts.baseUrl || './', import.meta.url);
  const t0 = performance.now();
  if (opts.onOutput) globalThis.__ironrubyWrite = opts.onOutput;
  const stdlibPromise = fetch(new URL(opts.stdlibUrl || 'stdlib.zip', base)).then(r => {
    if (!r.ok) throw new Error('stdlib.zip: HTTP ' + r.status);
    return r.arrayBuffer();
  });
  // In a Web Worker, dotnet.js takes a worker that already has an onmessage handler for one of
  // its own pthread workers and then waits forever for a main thread to hand it the assets.
  // dotnetSidecar marks this worker as an application worker that boots the runtime itself.
  if (typeof importScripts === 'function') globalThis.dotnetSidecar = true;
  const { dotnet } = await import(new URL('_framework/dotnet.js', base).href);
  let builder = dotnet;
  if (opts.dotnetConfig) builder = builder.withConfig(opts.dotnetConfig);
  const runtime = await builder.create();
  const config = runtime.getConfig();
  const exports = await runtime.getAssemblyExports(config.mainAssemblyName);
  const I = exports.IronRuby.Wasm.Interop;
  const tRuntime = performance.now();
  const stdlib = new Uint8Array(await stdlibPromise);
  const options = Object.assign({ streaming: !!opts.onOutput }, opts.options || {});
  const bootInfo = JSON.parse(I.Boot(stdlib, JSON.stringify(options)));
  bootInfo.dotnetMs = Math.round(tRuntime - t0);
  bootInfo.totalMs = Math.round(performance.now() - t0);
  if (!bootInfo.ok) throw new Error('IronRuby boot failed: ' + bootInfo.error);

  const api = {
    bootInfo,
    runtime,
    run(code, fileName) { return JSON.parse(I.Run(String(code), fileName || '')); },
    reset() { I.Reset(); },
    loadAssembly(bytes, fileName) {
      const res = JSON.parse(I.LoadAssembly(bytes instanceof Uint8Array ? bytes : new Uint8Array(bytes), fileName || ''));
      if (!res.ok) throw new Error('loadAssembly: ' + res.error);
      return res;
    },
    async loadAssemblyFromUrl(url, fileName) {
      const u = new URL(url, base);
      const r = await fetch(u);
      if (!r.ok) throw new Error(u + ': HTTP ' + r.status);
      return api.loadAssembly(new Uint8Array(await r.arrayBuffer()), fileName || u.pathname.split('/').pop());
    },
    writeFile(path, data) {
      I.WriteFile(path, typeof data === 'string' ? new TextEncoder().encode(data) : new Uint8Array(data));
    },
  };
  return api;
}
