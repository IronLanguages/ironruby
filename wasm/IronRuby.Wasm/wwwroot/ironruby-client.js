// Main-thread side of ironruby-worker.js.
//
//   import { createIronRubyWorker } from './ironruby-client.js';
//   const ir = await createIronRubyWorker({ onOutput: (fd, text) => term.write(text) });
//   const r = await ir.run('puts 6 * 7');          // {ok, result, stdout, stderr, ms, error?}
//   await ir.loadAssemblyFromUrl('/nuget/Markdig.dll');
//   ir.terminate();                                  // e.g. to stop an endless loop; boot again after
export async function createIronRubyWorker(opts = {}) {
  const workerUrl = opts.workerUrl || new URL('./ironruby-worker.js', import.meta.url);
  const worker = new Worker(workerUrl, { type: 'module' });
  let nextId = 1;
  const pending = new Map();
  worker.onmessage = (event) => {
    const msg = event.data;
    if (msg.type === 'output') { if (opts.onOutput) opts.onOutput(msg.fd, msg.text); return; }
    const p = pending.get(msg.id);
    if (!p) return;
    pending.delete(msg.id);
    msg.ok ? p.resolve(msg.value) : p.reject(new Error(msg.error));
  };
  worker.onerror = (e) => { for (const p of pending.values()) p.reject(new Error(e.message || 'worker error')); pending.clear(); };
  // after terminate() the worker answers nothing: a call made then is refused, not left waiting for ever
  let terminated = false;
  const call = (type, fields = {}, transfer = []) => new Promise((resolve, reject) => {
    if (terminated) { reject(new Error('terminated')); return; }
    const id = nextId++;
    pending.set(id, { resolve, reject });
    worker.postMessage(Object.assign({ id, type }, fields), transfer);
  });
  const { onOutput, workerUrl: _w, ...bootOpts } = opts;
  // A boot that fails (stdlib.zip not found, the runtime not starting) leaves nobody with a handle
  // to the worker: stop it here, or every retry adds another one.
  let bootInfo;
  try {
    bootInfo = await call('boot', { opts: bootOpts });
  } catch (e) {
    terminated = true;
    worker.terminate();
    pending.clear();
    throw e;
  }
  return {
    bootInfo,
    worker,
    run: (code, fileName) => call('run', { code: String(code), fileName }),
    reset: () => call('reset'),
    loadAssembly: (bytes, fileName) => call('loadAssembly', { bytes, fileName }),
    loadAssemblyFromUrl: (url, fileName) => call('loadAssemblyFromUrl', { url: String(new URL(url, location.href)), fileName }),
    writeFile: (path, data) => call('writeFile', { path, data }),
    terminate: () => { terminated = true; worker.terminate(); for (const p of pending.values()) p.reject(new Error('terminated')); pending.clear(); },
  };
}
