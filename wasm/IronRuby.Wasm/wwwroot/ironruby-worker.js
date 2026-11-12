// IronRuby in a Web Worker: runs never block the page, output arrives while the code runs, and a
// runaway loop is stopped with worker.terminate(). Use it through ironruby-client.js.
//
// Messages in:  {id, type: 'boot', opts} | {id, type: 'run', code, fileName} | {id, type: 'reset'}
//               {id, type: 'loadAssemblyFromUrl', url, fileName} | {id, type: 'loadAssembly', bytes, fileName}
//               {id, type: 'writeFile', path, data}
// Messages out: {type: 'output', fd, text} while running, then {id, ok: true, value} or {id, ok: false, error}
import { bootIronRuby } from './ironruby.js';

let ready = null;

self.onmessage = async (event) => {
  const msg = event.data || {};
  const reply = (value) => self.postMessage({ id: msg.id, ok: true, value });
  try {
    if (msg.type === 'boot') {
      ready = bootIronRuby(Object.assign({}, msg.opts, {
        onOutput: (fd, text) => self.postMessage({ type: 'output', fd, text }),
      }));
      reply((await ready).bootInfo);
      return;
    }
    if (!ready) throw new Error('IronRuby worker: send {type: "boot"} first');
    const ir = await ready;
    switch (msg.type) {
      case 'run': reply(ir.run(msg.code, msg.fileName)); break;
      case 'reset': ir.reset(); reply(null); break;
      case 'loadAssembly': reply(ir.loadAssembly(msg.bytes, msg.fileName)); break;
      case 'loadAssemblyFromUrl': reply(await ir.loadAssemblyFromUrl(msg.url, msg.fileName)); break;
      case 'writeFile': ir.writeFile(msg.path, msg.data); reply(null); break;
      default: throw new Error('IronRuby worker: unknown message ' + msg.type);
    }
  } catch (e) {
    self.postMessage({ id: msg.id, ok: false, error: String(e && e.stack || e) });
  }
};
