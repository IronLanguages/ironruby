# IronRuby in the browser (WebAssembly)

IronRuby, the DLR and the IronRuby libraries running on .NET 10's browser-wasm (Mono) runtime,
with the Prism parser compiled to WebAssembly. The bundle in `wasm/dist` is a set of static files
that any web server can serve. It exposes a small JavaScript API that works like a notebook
kernel: each run shares the top-level locals of the earlier ones, returns stdout, stderr and the
inspect of the last value, and can load .NET assemblies (for example NuGet DLLs) at run time.

```
wasm/
├── build.sh               full build -> dist/ (prism -> wasm, dotnet publish, stdlib.zip)
├── quick.sh               rebuild after a C# change, keeping dist/stdlib.zip
├── native/build-prism.sh  prism (C) -> native/build/prism.a with the SDK's emscripten
├── make-stdlib.py         Src/StdLib -> dist/stdlib.zip (what the browser can use)
├── IronRuby.Wasm/         the host: Interop.cs ([JSExport]s), boot.rb (the kernel), wwwroot/*.js
└── test/                  browser-test.mjs (Playwright), node-run.mjs, test pages, static server
```

## Build

Measured on the build box (headless Chromium, AOT bundle): about 1.6 s from `bootIronRuby()` to
ready (1.1 s of that is the `ruby4.rb` prelude), 2.8 s from page load. `fib(20)` takes 25 ms.
The interpreter-only bundle boots in about 5 s and runs Ruby code several times slower.

Needs the .NET 10 SDK with the `wasm-tools` workload (`dotnet workload install wasm-tools`), a
`prism` checkout next to the repository (`../prism`, as for the desktop build) and the DLR
sources in `../dlr`.

```sh
wasm/build.sh            # AOT build (default): about 8 minutes, boot about 1.5 s
AOT=0 wasm/build.sh      # IL only, on Mono's interpreter: 40 s to build, boot about 5 s
wasm/quick.sh            # after a C# change; AOT=0 works here too
```

`build.sh` copies `../dlr` to `../dlr-wasm` (the first time) and applies `Build/dlr-patches/*.patch`
and `Build/dlr-patches/wasm/*.patch` to that copy, so the desktop DLR checkout is not touched.
The AOT build compiles the hot assemblies (IronRuby, IronRuby.Libraries, the DLR, CoreLib,
System.Linq.Expressions and a few more; the list is `IronRubyAotAssemblies` in the csproj) to
WebAssembly. Everything else stays IL and runs on the interpreter. The AOT build has to be
trimmed. Trimming is `partial`: only framework assemblies are trimmed, and the framework
assemblies that Ruby code is likely to reach through interop are kept whole
(`IronRubyRootAssemblies`). The interpreter build is not trimmed, because Mono's interpreter
fails on a trimmed CoreLib. That is also why the build scripts delete `obj/` between builds.

## Test

```sh
wasm/test/fetch-fixtures.sh     # once: Markdig.dll from nuget.org into test/fixtures
node wasm/test/browser-test.mjs # headless Chromium via Playwright
node wasm/test/node-run.mjs 'puts 1 + 1' '@file.rb' '@dll:Some.dll'   # same bundle under Node
node wasm/test/serve.mjs 8016   # serve dist/ (with .br) to try index.html in a browser
```

`browser-test.mjs` boots the bundle on a page and checks: puts output (returned and streamed),
locals that persist across runs, classes/blocks/rescue, uncaught exceptions with class, message and
backtrace, pattern matching, .NET interop (`System::Math`, `List[Integer]`, `StringBuilder`,
`DateTime`/enums), `require "json"` and `"set"`, loading Markdig at run time and using it from
Ruby (including a pipeline with extensions), stderr, `exit`, `caller` and backtraces. Then it does
the same in a Web Worker: streamed output, a NuGet assembly, and an endless loop stopped with
`terminate()`.

## JavaScript API

```js
import { bootIronRuby } from './ironruby.js';           // runs on the calling thread

const ir = await bootIronRuby({
  onOutput: (fd, text) => console.log(fd, text),         // optional: output while code runs
  // baseUrl: '/ironruby/',  stdlibUrl: '...',  options: { prelude: true }
});
ir.bootInfo                 // {ok, version, ms, timings, dotnetMs, totalMs}
ir.run('x = 6 * 7')         // {ok: true, result: "42", stdout: "", stderr: "", ms}
ir.run('puts x')            // locals persist: {ok: true, result: "nil", stdout: "42\n", ...}
ir.run('1 / 0')             // {ok: false, error: {class: "ZeroDivisionError", message, backtrace}, ...}
ir.run('exit 3')            // {ok: true, result: null, exit: 3} - the runtime stays usable
ir.reset()                  // forget the locals (methods, classes and constants stay)

await ir.loadAssemblyFromUrl('/nuget/Markdig.dll');      // or ir.loadAssembly(uint8Array, 'Markdig.dll')
// -> {ok, name: "Markdig", fullName, types, namespaces: [...]}
ir.run('Markdig::Markdown.ToHtml("# Hi *there*").to_s')
ir.writeFile('/ironruby/lib/greeter.rb', 'def greet = "hi"')   // then: require "greeter"
```

A run blocks the thread it runs on. A page that has to stay responsive, or that has to stop
endless loops, should use the worker version, which has the same calls, all asynchronous:

```js
import { createIronRubyWorker } from './ironruby-client.js';
const ir = await createIronRubyWorker({ onOutput: (fd, text) => term.write(text) });
const r = await ir.run('3.times { puts _1 }');
await ir.loadAssemblyFromUrl('/nuget/Markdig.dll');
ir.terminate();             // stops a runaway loop; pending calls reject with "terminated"
```

Loaded assemblies go into the default load context, so they can reference each other (load the
dependencies first), and their namespaces become Ruby constants right away. They are also written
to `/ironruby/assemblies`, which is on `$LOAD_PATH`, so `require "Markdig"` and
`load_assembly "Markdig"` work too. Use the `lib/net8.0` (or `netstandard2.0`) DLL of a NuGet
package. Pure managed code works. P/Invoke into native libraries does not.

## What is in the bundle

- `_framework/`: the runtime (`dotnet.native.wasm`, which includes prism and the AOT code) and
  the assemblies as webcil `.wasm` files, each also as `.br` and `.gz`. A server that serves the
  precompressed files (`Content-Encoding: br`, as `test/serve.mjs` does, or nginx `gzip_static` /
  `brotli_static`) sends about 13.5 MB brotli or 20 MB gzip. Uncompressed it is about 63 MB. No
  `.pdb` files are published.
- `stdlib.zip` (about 5.4 MB, stored): unpacked into the in-memory file system at boot.
  `/ironruby/stdlib` holds the IronRuby and 4.0 libraries without what needs processes,
  sockets or RubyGems (rubygems, bundler, irb, rdoc, webrick, drb, puma, ffi, ...).
  `/ironruby/stdlib/bundled-gems` holds the pure-Ruby bundled gems (ostruct, prime, csv, matrix,
  logger, rexml, minitest, test-unit, ...) in one flat directory on `$LOAD_PATH`.
- RubyGems is not loaded (like `--disable-gems`). The `ruby4.rb` prelude is.

## Limitations

- Single-threaded: `Thread.new`, `Fiber` and external enumerators (`Enumerator#next`, which is built
  on threads) raise NotImplementedError, and so does what is built on them (`CSV.parse`, for one). Internal iteration (`each`, `map`, lazy enumerators that
  do not call `next`) works. Background compilation of hot code runs synchronously instead.
- No processes, sockets or native extensions: backticks, `system`, `fork`, `Socket`, `ffi`, `fiddle`.
  Native-backed libraries (sqlite3 through SQLitePCLRaw) are absent or fail when used.
  `Process.times` reports wall time since boot.
- No psych/YAML yet (IronRuby.Libraries.Yaml is not part of the bundle). `Digest::MD5` throws,
  because the browser has no MD5. SHA1/SHA256 work through the browser's own implementations.
- The file system is emscripten's in-memory one. It is empty apart from `/ironruby` and is lost on reload.
- With AOT there are no CLR stack frames, so Ruby backtraces come from the DLR interpreter's
  frame chain (see `RubyStackTraceBuilder.ClrStackAvailable`). Frames of code the DLR compiled can
  be missing from them. The prelude's `require_relative` and `__dir__` take this into account. A
  library that reads `caller` from a hot method may still see a frame too few.
- In a module Web Worker, `ironruby.js` sets `globalThis.dotnetSidecar = true` before it loads
  `dotnet.js`. Without that, dotnet.js takes any worker that already has an `onmessage` handler for
  one of its own pthread workers and waits forever.
- Not wasm-specific, but noticeable here: once any code has run `refine Object` (rexml does, in
  `rexml/xpath_parser`), method calls everywhere get about 50x slower. The desktop build does the
  same. `fib(20)` goes from about 25 ms to 400 ms.
