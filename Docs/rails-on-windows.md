# Rails 8.1 + SQLite and popular gems on IronRuby, on Windows

Measured on `fix/llm-port-quirks` merged up to `origin/master` (`b34a4df9`), Windows 11,
.NET 10.0.12, Debug build, 2026-10-27, with a repo-local `GEM_HOME` (`.gems`) and
`IRONRUBY_NO_HOST_GEMS=1`. The legacy parser was removed in the same piece of work (see the
last section); everything below was re-checked on the prism-only build.

## Rails

The path a Windows user would take:

```
gem install rails                  # 8.1.3.1, 43 gems
rails new blog                     # default Gemfile, untouched
bundle install                     # 97 gems: Puma, sqlite3, nokogiri, bootsnap, ... as resolved
bin/rails generate scaffold Post title:string body:text published:boolean
bin/rails db:migrate
bin/rails importmap:install turbo:install stimulus:install
bin/rails server
```

The app serves the scaffold through IronRuby's Puma, against SQLite through IronRuby's
sqlite3, with Turbo and Stimulus loaded through importmap and propshaft. Index, new, create
(with a CSRF token), show, JSON, edit, update, destroy and the development 404 page answer as on
CRuby, the CSRF meta tags are there, and a POST without a token is refused with 422.

### Fixed here

1. **`gem install rails` died writing the first binstub: `File#flock` raised
   `NotImplementedError` on Windows.** RubyGems locks every binstub it writes. Now
   `LockFileEx`/`UnlockFileEx` over the whole file, as CRuby's win32.c does it, polled for a
   blocking lock like the Unix path. `Src/Libraries/Builtins/FileOps.cs`.
2. **`bundle install` died on load: `Process.getrlimit` P/Invoked libc.** net-http-persistent
   (vendored by Bundler) calls it whenever `Process::RLIMIT_NOFILE` is defined. On Windows CRuby
   has neither the `RLIMIT_*`/`RLIM_*`/`PRIO_*` constants nor working `getrlimit`/`setrlimit`/
   `getpriority`/`setpriority` (they raise `NotImplementedError`; `respond_to?` says false).
   IronRuby now matches. `Src/StdLib/ironruby/ruby4.rb`.
3. **`bundle install` died checksumming the first `.gem`: `IO#readpartial` after `IO#eof?`
   P/Invoked libc `poll`.** New `IoReadiness.IsReadableNow`: poll on Unix; on Windows
   `Socket.Poll`, `PeekNamedPipe` for a pipe, and a disk file is always readable.
   `Src/Libraries/Builtins/IoReadiness.cs`, `IoOps.cs`.
4. **`bin/rails` refused to run: "no Ruby script found in input".** Rails writes binstubs from
   `ruby_install_name`, which is `ir`, so they start `#!/usr/bin/env ir.exe"`; like MRI, the
   command line and prism skip a `#!` line without "ruby" in it. A `#!` line naming IronRuby's
   own launcher now counts as Ruby. `RubyUtils.ShebangNamesIronRuby`, `RubyCommandLine.cs`,
   `PrismAstBridge.cs`. Since then the launcher is called `ironruby`, as JRuby's is `jruby`,
   and so is `ruby_install_name`: new binstubs say `#!/usr/bin/env ironruby` and need no
   special case; this one keeps those written with `ir` running.
5. **`TZInfo::DataSourceNotFound` in the first initializer.** Windows has no zoneinfo, so the
   Gemfile has `gem "tzinfo-data", platforms: %i[windows jruby]`; Bundler filters such gems by the
   *generic* platform, and IronRuby's `universal-dotnet` is generic `ruby`. For the dotnet
   platform the restriction is now answered by the running Ruby. `ironruby/gem_compat.rb`.
6. **`private method 'importmap=' called`.** importmap-rails runs
   `Rails::Application.send(:attr_accessor, :importmap)` at a file's top level, where the default
   visibility is private; MRI applies the scope's visibility only when the caller's `self` is the
   module itself. `Src/Libraries/Builtins/ModuleOps.cs`.
7. **Every development error page was a 500**: web-console calls `Exception#bindings`, which
   bindex defines for CRuby, JRuby and Rubinius only. It now answers `[]` as it does there for an
   exception nobody captured bindings for, so the page renders. `ironruby/gem_compat.rb`.
8. **"fiddle used to be loaded from the standard library" on every boot**, from IronRuby's own
   `ffi.rb`. fiddle (like syslog, nkf, win32ole) is IronRuby's own, in its archdir, and is not
   leaving. `Src/StdLib/ruby/4.0/bundled_gems.rb`.

Found here and already fixed on master: `Pathname#binwrite` missing (master now ships CRuby
4.0's `pathname_builtin.rb`), and a `Hash` subclass's `super() { |h, k| ... }` losing the default
proc - which made `ActiveSupport::InheritableOptions` return `nil` for inherited config, so
**CSRF protection was silently off** in every controller.

### Not IronRuby's to fix: a checkout path with spaces

The checkout these measurements come from lives in a path with spaces, and two things break there exactly as
they would for CRuby installed in such a path: Bundler sets `RUBYOPT=-r<path>/bundler/setup`
unquoted, so a child Ruby started from a bundled process splits it (`invalid switch in RUBYOPT:
-A`; this stops `turbo:install`/`stimulus:install`), and Rails runs `"#{Gem.ruby}" bundle
install` around a `Gem.ruby` that is already quoted. Run those install templates with
`bin/rails app:template LOCATION=...`, or keep IronRuby in a path without spaces. mspec's
`ruby_exe` has the same problem: it joins the interpreter, temp files and fixtures into a shell
command unquoted, so from a path with spaces most child-interpreter specs fail. Running with every
path in its 8.3 short form (`RUBY_EXE`, `SPEC_TEMP_DIR` and the spec files) avoids it.

## Popular gems

`tmp/rails-try/gem_sweep.rb` (not committed) runs the popular tier of `Util/gems/catalog.rb`
the way `Util/gems/run.sh` does on Linux: each `Util/gems/exercises/<gem>.rb` under IronRuby
and under CRuby 4.0.1 with the same gem versions, byte-identical stdout being the pass.

- **81 work, identical to CRuby**, among them railties, activesupport, activerecord,
  actionpack, actionview, activejob, activemodel, sqlite3, nokogiri, rack, sinatra, roda, sidekiq,
  faraday, excon, httpclient, webmock, vcr, aws-sdk-core, jwt, mail, parser, concurrent-ruby,
  zeitwerk, i18n, thor, tzinfo, kramdown, asciidoctor, octokit, chunky_png, listen, and - after
  the fixes below - rspec, pry, sprockets, method_source, tty-prompt, rubocop, rubocop-ast,
  standard and prawn. (The CRuby side needs `tzinfo-data` on Windows, `webrick`, and for pry `readline`
  with CRuby's own `fiddle`.)
- **19 load** and have no exercise (puma, nio4r, ffi, oj, bcrypt, msgpack, websocket-driver,
  yard, googleauth, rails-dom-testing, ...).
- Fixed here: **tty-prompt** - `IO#ioctl` on Windows raised .NET's `NotSupportedException`;
  CRuby defines it as a not-implemented stub (`NotImplementedError`, `respond_to?` false), which
  tty-screen asks about. `[RubyNotImplemented(OnWindowsOnly = true)]`, `IoOps.cs`.
  And opening a path with characters Windows forbids (`<`, `|`, `?`) raised a bare `IOError`;
  it is `Errno::EINVAL` now, as in CRuby. `RubyFile.cs`.
- Fixed here: **rspec** - its failure report goes through Ripper, and IronRuby's Ripper read a
  Symbol's name from `value_loc`, which prism 2.0 calls `content_loc`; every Symbol raised.
  `Src/StdLib/ironruby/ripper/sexp_builder.rb`.
- Fixed here: **sprockets** - `Pathname#absolute?` was false for every `C:/...` path.
  `pathname_builtin.rb` (CRuby 4.0's own) recognises drive letters by
  `File.dirname("A:") == "A:."`, and IronRuby answered `"A:"`: `File.dirname`/`basename` of
  a drive-relative name (`C:`, `C:x`) now give MRI's `C:.` and `x`. Also from ruby/spec: three
  or more leading separators are a UNC prefix (`File.dirname("\\\\\\foo")` is `\\foo`), and a
  UNC root's base name keeps the separator it ends with. `FileOps.cs`.
- Fixed here: **method_source** - `Method#source_location` of a method IronRuby's prelude
  defines was `<internal:...>` even where MRI's is C (`Kernel#puts`), and method_source then
  tried to open the file. It is now `nil` there, and MRI's `<internal:...>` name where MRI
  writes the method in Ruby too; `Util/gen-internal-methods.rb` generates that list from
  CRuby (`InternalMethods.Generated.cs`, 314 methods).
- Fixed here: **pry** - `File.realpath` did not raise `Errno::ENOENT` for a path that is not
  there (nor `realdirpath` for a missing parent) where there is no realpath(3) - Windows, macOS -
  so pry tried to load a `./.pryrc` that does not exist. `FileOps.cs`.
- Fixed here: **rubocop, rubocop-ast, standard, and minitest 6** (which a Bundler-less
  `require "rails-dom-testing"` picks) could not activate: they ask for `prism ~> 1.x`, and
  IronRuby's built-in prism is 2.0.0 (the honest version of the ruby/prism `main` it pins), the
  only one it can have - the gem is a C extension. They use prism through its Ruby API, which 2.0
  kept for what they call, so RubyGems now reads a requirement *on prism only* for 1.x with its
  upper bound lifted (`~> 1.7` as `>= 1.7`), and only where the built-in prism then satisfies
  it; a 0.x requirement is left alone. Bundler goes through the same `Gem::Dependency`: a
  Gemfile with `minitest` and `rubocop` resolves to minitest 6.0.6 and rubocop 1.92.1 on
  prism 2.0.0. `rubygems/defaults/ironruby.rb`.
- Fixed here: **prawn**, the same way: ttfunk, which prawn depends on, asks for
  `bigdecimal ~> 3.1`, and IronRuby's bigdecimal is its own C# one at 4.0.1. The rule above
  now covers bigdecimal from 2.x on (2.0 removed `BigDecimal.new`), so prawn activates with the
  built-in one, under Bundler too, and renders the same PDF bytes as CRuby with the C gem 3.3.1.
  Substituting bigdecimal-pure was looked at and is not needed here: IronRuby's C# BigDecimal
  passes ruby/spec's `library/bigdecimal` at CI's revision but for two examples that start a
  child interpreter and fail from a path with spaces (bigdecimal-pure on IronRuby fails 7
  more), and what ttfunk lacked was a version it accepts, not an implementation. New
  exercise `Util/gems/exercises/prawn.rb`.
- Fixed here: **rubocop showed every file by its absolute path.** `Dir.pwd` expanded 8.3 short
  names (`C:\Users\ABCDEF~1`), so after `Dir.chdir(Dir.tmpdir)` it was not the directory just
  changed into, and no file path built from the one started with the other. `Dir.pwd` is now
  `GetCurrentDirectory`, and `Dir.chdir` sets the path as given, as MRI does.
  `RubyConsoleHost.cs`.
- Fixed here: **listen's exercise was flaky** (1 run in 3): listen builds its first snapshot on a
  thread of its own after `#start`, so a file written at once can land in it and no "added"
  event comes, on any Ruby. The exercise now writes the file again until an event names it.
  `Util/gems/exercises/listen.rb`.
- **Expected**: pg, mysql2, sassc, google-protobuf (native code), rb-inotify (Linux only).

## Open

- **NameError/NoMethodError messages quote names the 3.3 way**, `` `b' ``, where Ruby 3.4+
  writes `'b'`.
- **The "block passed to 'y' may be ignored" warning** says `'y'` where CRuby says
  `'Object#y'`, and is sometimes reported at line 0.
- **web-console's console has no bindings to evaluate in** (fix 7): real `Exception#bindings`
  needs the runtime to capture a Binding per frame at raise time.
- **`File.write` in text mode returns the byte count after CRLF conversion** (CRuby: before).
- **`Pathname` methods report `pathname_builtin.rb`'s real path** as their `source_location`,
  where CRuby says `<internal:pathname_builtin>`.
- **`IO#read_nonblock` on an empty pipe blocks** on Windows (the README's pipe-readiness gap).
- **concurrent-ruby warns "Possibly unsupported Ruby implementation"** on every Rails command:
  it knows MRI, JRuby and TruffleRuby only; `Concurrent::Array`/`Hash` are then plain
  `Array`/`Hash` on a runtime with parallel threads.
- **Every `bin/rails` command takes 40-70 s** to boot on the Debug build.
- One `bundle install` once locked `irb 1.16.0` from rubygems.org instead of IronRuby's pinned
  default gem (`Bundler::GemNotFound` afterwards); three later resolutions pinned correctly.

## The legacy parser is gone

Prism is IronRuby's only parser. Removed: the gppg grammar (`Parser.y`, `Parser.Generated.cs`,
`GPPG.cs`, `ParserStack.cs`), the lexer half of `Tokenizer.cs` (its static helpers for names,
number literals and the encoding magic comment stay), `RubyContext.LegacyParser` and the
fallback to it, the legacy `-n`/`-p` wrapping, the DLR `TokenizerService` (the colouring
tokenizer was that lexer), and the test suite's parser/tokenizer tests and `/tokenizer` mode.
`-X:UsePrism` is still accepted and ignored; the launchers no longer pass it. 17,939 lines go.

The C# test suite now runs every test on prism and passes (before, most ran on the legacy
parser). Of the ~70 tests that failed when switched over:

- 15 tested 1.8/1.9-only control flow (`retry` outside `rescue`, `break`/`next`/`redo`/`retry`
  escaping an `eval`) that is a SyntaxError in CRuby 4; they were removed.
- About 45 now expect what CRuby 4 does: the suite runs with warnings on, so each expects the
  parse warnings `ruby -wc` gives, and a handful of outputs changed to CRuby's (a `BEGIN` block
  sees top-level locals as `nil`; `defined?` never calls `const_missing`; a regexp literal calls
  a redefined `Regexp#=~`; Ruby 3.4 rejects keywords in an index assignment).
- Two were IronRuby bugs on the prism path, fixed: a `BEGIN` nested in a `BEGIN` ran after the
  outer one's first statement (MRI runs it first; `PrismAstBridge.HoistPreExecution`), and
  `defined?(A::B::C)` called `const_missing` for a missing name in the middle of the path
  (`RubyOps.ResolveQualifiedConstant`).

## ruby/spec `spec/language`

Run at CI's pinned revisions (`SPEC_REF`/`MSPEC_REF` in `.github/workflows/ci.yml`), checked
out with LF line endings as CI does (`core.autocrlf input`; a CRLF checkout moves the `DATA`
offsets three specs compare), the language suite now passes on Windows: every one of its 2,921
examples passes, run once with short paths for the child-interpreter specs and once from the
long path for the three that compare `__FILE__` and `$:` with paths resolved from the current
directory. CI on master had two left, both fixed here:

- **`:"#{...}"` and `%I[#{...}]` made a Symbol of invalid bytes** where MRI raises
  `EncodingError` (as `String#to_sym` already did). `RubyOps.CreateInterpolatedSymbol`.
- **`/^(()|a)*?$/` overflowed the backtracking stack on .NET 10** (an error on the net10.0 spec
  job): the loop Onigmo's empty-iteration rule is emulated with is now a counted lazy loop,
  `{0,N}?`, which .NET 10 stops as Onigmo does. `RegexpTransformer.RewriteLoop`.

And one that only showed in a long run: **`eval("__FILE__")` said `(eval at :0)`** once a
library frame stood first in the backtrace; the eval's location is now the first frame that
names a file. `RubyContext.TryGetCurrentSourceLocation`.

## Paths and encodings on Windows

The `File`, `Dir` and `Encoding` specs that failed on Windows before (6 failures, 2 errors) pass:

- **Paths were Windows-1252.** The filesystem and locale encodings, `Encoding.default_external`,
  and with them `ENV`, `ARGV`, `Dir.home` and every path IronRuby hands out, are UTF-8 on
  Windows, as in CRuby 3.0+. `RubyContext.cs`.
- **`Dir.home` and `File.expand_path("~")` had backslashes** (`C:\Users\...` straight from
  `USERPROFILE`); CRuby gives `/` and looks at `HOME`, then `USERPROFILE`, then
  `HOMEDRIVE`+`HOMEPATH`, then the profile folder. `Dir.home` no longer modifies the `HOME`
  string it was given. `ironruby/ruby4.rb`.

Noted: `RbConfig::CONFIG["ruby_version"]` is still `1.9.1` (and `sitelibdir` ends in
`site_ruby/1.9.1`), where CRuby 4 says `4.0.0`.
