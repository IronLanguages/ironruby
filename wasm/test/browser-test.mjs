// Boots wasm/dist in headless Chromium (Playwright) and checks the interpreter end to end.
//   node wasm/test/browser-test.mjs        (run wasm/test/fetch-fixtures.sh once first)
import path from 'node:path';
import fs from 'node:fs';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';
import { serve } from './serve.mjs';

const require = createRequire(process.env.PLAYWRIGHT_MODULE_DIR || '/usr/local/lib/node_modules/');
const { chromium } = require('playwright');
const here = path.dirname(fileURLToPath(import.meta.url));
const dist = process.env.IRW_DIST || path.join(here, '..', 'dist');
if (!fs.existsSync(path.join(here, 'fixtures', 'Markdig.dll'))) {
  console.error('missing test/fixtures/Markdig.dll - run wasm/test/fetch-fixtures.sh');
  process.exit(2);
}

const server = await serve({ '/test/': here, '/': dist });
const base = `http://127.0.0.1:${server.address().port}`;
const browser = await chromium.launch();
const page = await browser.newPage();
const pageErrors = [];
page.on('pageerror', e => pageErrors.push(e.message));
page.on('console', m => { if (m.type() === 'error') console.log('[console.error]', m.text().slice(0, 500)); });

let failures = 0, passes = 0;
function check(name, cond, detail) {
  if (cond) { passes++; console.log(`ok   ${name}`); }
  else { failures++; console.log(`FAIL ${name}\n     ${typeof detail === 'string' ? detail : JSON.stringify(detail)}`); }
}
const run = (code) => page.evaluate(c => window.ir.run(c), code);

try {
  const t0 = Date.now();
  await page.goto(base + '/test/index.html');
  const boot = await page.evaluate(() => window.irReady);
  console.log(`boot: ${Date.now() - t0} ms wall (page load to ready); ${JSON.stringify(boot)}`);
  check('boots', boot.ok && /IronRuby/.test(boot.version), boot);

  let r = await run('puts "Hello from #{RUBY_ENGINE} #{RUBY_VERSION}"');
  check('puts output', r.ok && r.stdout === 'Hello from ironruby 4.0.0\n', r);
  const streamed = await page.evaluate(() => window.streamed.map(x => x[1]).join(''));
  check('output streamed to onOutput', streamed.includes('Hello from ironruby'), streamed);

  r = await run('x = 20');
  r = await run('x * 2 + 2');
  check('locals persist across runs', r.ok && r.result === '42', r);
  r = await run('def twice = yield(yield(1))\ntwice { _1 + x }');
  check('methods persist and see nothing but their own scope; blocks close over x', r.ok && r.result === '41', r);

  r = await run(`
class Shape
  attr_reader :name
  def initialize(name) = @name = name
  def area = raise(NotImplementedError, "#{self.class}#area")
  def to_s = "#{name} (#{format('%.2f', area)})"
end
class Circle < Shape
  def initialize(r) = (super("circle"); @r = r)
  def area = Math::PI * @r ** 2
end
class Square < Shape
  def initialize(s) = (super("square"); @s = s)
  def area = @s * @s
end
shapes = [Circle.new(1), Square.new(2), Shape.new("blob")]
shapes.each do |s|
  puts s
rescue NotImplementedError => e
  puts "no area: #{e.message}"
end
shapes.first(2).sum(&:area).round(3)`);
  check('classes, blocks, rescue', r.ok && r.stdout === 'circle (3.14)\nsquare (4.00)\nno area: Shape#area\n' && r.result === '7.142', r);

  r = await run('def divide(a, b) = a / b\ndivide(1, 0)');
  check('uncaught exception reported', !r.ok && r.error.class === 'ZeroDivisionError' && r.error.backtrace.some(l => /cell\d+:1/.test(l)), r);

  r = await run('case {name: "Ruby", tags: [1, 2]}\nin {name: String => n, tags: [*, 2]} then "matched #{n}"\nend');
  check('pattern matching (prism front end)', r.ok && r.result === '"matched Ruby"', r);

  r = await run('System::Math.Sqrt(16.0) + System::Math.Max(1, 2)');
  check('.NET static call (System::Math)', r.ok && r.result === '6.0', r);
  r = await run('l = System::Collections::Generic::List[Integer].new\n[5, 3, 9].each { l.Add(_1) }\nl.Sort\n[l.Count, l.to_a]');
  check('.NET generic List[Integer]', r.ok && r.result === '[3, [3, 5, 9]]', r);
  r = await run('sb = System::Text::StringBuilder.new\n3.times { |i| sb.Append("ab").Append(i) }\nsb.ToString.to_s.upcase');
  check('.NET StringBuilder', r.ok && r.result === '"AB0AB1AB2"', r);
  r = await run('System::DateTime.new(2026, 10, 17).DayOfWeek.to_s');
  check('.NET enum', r.ok && r.result === '"Saturday"', r);

  r = await run('require "json"\nJSON.generate({a: [1, 2.5, nil]})');
  check('require json', r.ok && r.result === '"{\\"a\\":[1,2.5,null]}"', r);
  r = await run('require "set"\nSet[3, 1, 3].size');
  check('Set', r.ok && r.result === '2', r);

  const info = await page.evaluate(() => window.ir.loadAssemblyFromUrl('/test/fixtures/Markdig.dll'));
  check('load NuGet assembly (Markdig) at run time', info.ok && info.name === 'Markdig', info);
  r = await run('require "Markdig"\nMarkdig::Markdown.ToHtml("# Hi *there*\\n\\n- a\\n- b").to_s');
  check('use Markdig from Ruby', r.ok && r.result.includes('<h1>Hi <em>there</em></h1>') && r.result.includes('<li>a</li>'), r);
  r = await run('pipeline = Markdig::MarkdownPipelineBuilder.new\npipeline = Markdig::MarkdownExtensions.UseAdvancedExtensions(pipeline).Build\nMarkdig::Markdown.ToHtml("| a | b |\\n|---|---|\\n| 1 | 2 |", pipeline).to_s');
  check('Markdig pipeline with extensions (tables)', r.ok && r.result.includes('<table>'), r);

  r = await run('$stderr.puts "warn!"; 1');
  check('stderr captured', r.ok && r.stderr === 'warn!\n', r);
  r = await run('exit 3');
  check('exit is reported, not fatal', r.ok && r.exit === 3, r);
  r = await run('x');
  check('still alive after exit', r.ok && r.result === '20', r);

  const t1 = Date.now();
  r = await run('def fib(n) = n < 2 ? n : fib(n - 1) + fib(n - 2)\nfib(20)');
  check('fib(20)', r.ok && r.result === '6765', r);
  console.log(`fib(20): ${Date.now() - t1} ms`);

  r = await run('def who = caller.first\nwho');
  check('caller', r.ok && /cell\d+:2/.test(r.result), r);
  r = await run('def boom = raise("deep")\ndef mid = boom\nbegin; mid; rescue => e; e.backtrace.first(3); end');
  check('backtrace of a rescued exception', r.ok && /cell\d+:1:in 'Object#boom'/.test(r.result) && /cell\d+:2:in 'Object#mid'/.test(r.result), r);

  // after fib: loading rexml (its refinements) slows later method calls down, on the desktop too
  r = await run('require "ostruct"\nrequire "rexml/document"\ndoc = REXML::Document.new("<a><b>x</b></a>")\n[OpenStruct.new(v: 1).v, doc.root.elements["b"].text]');
  check('bundled gems (ostruct, rexml: dozens of nested require_relative)', r.ok && r.result === '[1, "x"]', r);

  check('no page errors', pageErrors.length === 0, pageErrors);

  // The same bundle in a Web Worker (ironruby-client.js / ironruby-worker.js).
  const wpage = await browser.newPage();
  wpage.on('pageerror', e => pageErrors.push('worker page: ' + e.message));
  await wpage.goto(base + '/test/worker.html');
  const wboot = await wpage.evaluate(() => window.irReady);
  check('worker boots', wboot.ok, wboot);
  const t2 = Date.now();
  const wr = await wpage.evaluate(async () => {
    const started = performance.now();
    const r = await window.ir.run('3.times { |i| puts "tick #{i}" }\n(1..20000).sum');
    return { r, firstOutputAfter: window.output.length ? window.output[0].t - started : null, output: window.output.map(o => o.text).join('') };
  });
  check('worker run + streamed output', wr.r.ok && wr.r.result === '200010000' && wr.output === 'tick 0\ntick 1\ntick 2\n', wr);
  const wl = await wpage.evaluate(async () => {
    const info = await window.ir.loadAssemblyFromUrl('/test/fixtures/Markdig.dll');
    return [info, await window.ir.run('Markdig::Markdown.ToHtml("*hi*").to_s')];
  });
  check('worker loads a NuGet assembly', wl[0].ok && wl[1].ok && wl[1].result.includes('<em>hi</em>'), wl);
  console.log(`worker run: ${Date.now() - t2} ms`);
  // evaluate() has no timeout of its own: were terminate() to leave the call pending, this script would
  // wait for the job's time limit instead of reporting the failure
  const within = (ms, what, promise) => Promise.race([promise,
    new Promise((_, reject) => setTimeout(() => reject(new Error(`${what}: no answer in ${ms} ms`)), ms).unref())]);
  const wt = await within(30000, 'terminate()', wpage.evaluate(async () => {
    const looping = window.ir.run('loop { }').then(() => 'finished', e => e.message);
    await new Promise(r => setTimeout(r, 300));
    window.ir.terminate();
    return looping;
  }));
  check('worker: endless loop stopped by terminate()', wt === 'terminated', wt);

  // reset: the locals of the session before are gone (they used to stay visible, as the new binding was a
  // block's inside the old one), methods and constants stay, and a frame still reads <main>
  await run('x = 20; def kept = :kept; KEPT = 1');
  await page.evaluate(() => window.ir.reset());
  r = await run('def after_reset = 1; [after_reset, Object.new.respond_to?(:after_reset), Object.private_method_defined?(:after_reset)]');
  check('a def after reset is private, as at the top level', r.ok && r.result === '[1, false, true]', r);
  r = await run('[defined?(x), kept, KEPT]');
  check('reset drops locals, keeps methods and constants', r.ok && r.result === '[nil, :kept, 1]', r);
  r = await run('y = 1; raise "after reset"');
  check('a frame after reset reads <main>, not the top level of the session file', !r.ok && r.error.message === 'after reset' && !/top \(required\)/.test(JSON.stringify(r)) && r.error.backtrace.some(l => l.includes("in '<main>'")), r);

  // a call after terminate() is refused at once (it used to wait for an answer that never comes)
  const late = await within(30000, 'a call after terminate()', wpage.evaluate(() => window.ir.run('1').then(() => 'finished', e => e.message)));
  check('worker: a call after terminate() is rejected', late === 'terminated', late);
} catch (e) {
  failures++;
  console.log('FAIL exception: ' + (e.stack || e));
} finally {
  await browser.close();
  server.close();
}
console.log(`\n${passes} passed, ${failures} failed`);
process.exit(failures ? 1 : 0);
