# The most popular gems on rubygems.org, under IronRuby

How far down rubygems.org's own most-downloaded list IronRuby gets, and what
stops it where it stops.

## Method

The list is rubygems.org/stats, which publishes the top 100 by all-time
downloads, ten to a page. 77 of those 100 are in the popular tier of
`Util/gems/catalog.rb` and are measured here. The other 23 are accounted for at
the bottom of this file; none of them is left out because it failed.

Every gem is installed from rubygems.org with IronRuby's own RubyGems:

```sh
export GEM_HOME="$PWD/.gems"      # never the shared user gem dir
Util/gems/install-popular.sh
Util/gems/run.sh popular
```

For each gem the harness requires it, and then:

* runs the gem's own test suite, if the `.gem` ships one. Almost none do - a
  `.gem` is the shipped library, not the repository - so this is rare;
* otherwise runs `Util/gems/exercises/<gem>.rb`, a small but real use of the
  gem, under **both IronRuby and CRuby 4.0.6**, and diffs stdout. CRuby is the
  oracle and byte-identical output is the pass.

The exercises are not smoke tests. They boot a Rails application and serve it a
request, run ActiveRecord against a real in-memory database, render an ERB
template through ActionView, sign an AWS request and parse a stubbed response,
run `rubocop --format emacs` over a file with a real offence and compare the
report, drive a Pry session from a string, build a PNG and decode it again,
fetch over a loopback WEBrick. `Util/gems/exercises/README.md` says what an
exercise has to do to count.

A gem that only loads is reported as "load only", which is not a pass, and the
table says so.

## Status

WORKS means the exercise (or the gem's own suite) produced byte-identical
output to CRuby 4.0.6. The rank is the gem's position in the top-100 list.

| # | Gem | Version | Status | Evidence / reason |
|--:|-----|---------|--------|-------------------|
| 2 | aws-sdk-core | 3.257.0 | **WORKS** | Sigv4 signing, credentials, a stubbed STS client and its error mapping. Needed the `SSLErrorWaitReadable`, `StringIO#read(n, nil)` and Nokogiri SAX fixes |
| 3 | aws-sigv4 | 1.12.1 | **WORKS** | the signing itself, exercised through aws-sdk-core |
| 4 | aws-partitions | 1.1287.0 | load only | data for aws-sdk-*; nothing to exercise on its own |
| 5 | jmespath | 1.6.2 | **WORKS** | 16 JMESPath expressions over a nested document: filters, projections, multiselect, functions |
| 6 | i18n | 1.15.2 | **WORKS** | translation, interpolation, pluralisation, defaults, locale switching, both error classes. Needs the Monitor patch in `ironruby/gem_compat.rb` |
| 7 | activesupport | 8.1.3.1 | **WORKS** | inflections, Hash/Array extensions, durations, `Time#advance`, JSON encoding, `Notifications`, `Cache::MemoryStore`, `Digest` |
| 10 | concurrent-ruby | 1.3.8 | **WORKS** | `Concurrent::Map` with a default proc, `AtomicFixnum` across four threads, `Promises`, `Delay`, `CountDownLatch`. Needs the Monitor patch |
| 11 | aws-eventstream | 1.4.0 | load only | a codec for aws-sdk-*'s streaming protocols |
| 12 | rack | 3.2.7 | **WORKS** | `Rack::Builder`, `map`, middleware, `Request`/`Response`, nested query parsing, `MockRequest` |
| 13 | tzinfo | 2.0.6 | **WORKS** | zone lookup, DST transitions, local↔UTC conversion, country data, the invalid-identifier error |
| 15 | addressable | 2.9.0 | **WORKS** | parsing, normalisation, IDN, templates, `route_from`/`route_to`, query values |
| 16 | nokogiri | IronRuby's own | **WORKS** | CSS and XPath over HTML and XML, attributes, mutation, fragments, SAX. `Src/Libraries/Nokogiri` over AngleSharp; the C-extension gem does not install |
| 17 | faraday | 2.14.4 | **WORKS** | the middleware stack over Faraday's test adapter, `raise_error`, nested query encoding |
| 18 | public_suffix | 7.0.5 | **WORKS** | the real PSL: TLD/SLD/TRD splits, private domains, wildcard rules, both error classes |
| 19 | diff-lcs | 2.0.0 | **WORKS** | `lcs`, `diff`, `sdiff`, `patch`, `unpatch!`, a unified hunk |
| 24 | multi_json | 1.21.2 | **WORKS** | dump/load/pretty over the json adapter, `ParseError`. Needed the `JSON.generate(obj, state)` arity fix |
| 25 | thor | 1.5.0 | **WORKS** | a command class with options, subcommands, shell tables, `InvocationError`. Needed the `Regexp.last_match(-1)` fix |
| 27 | ffi | 1.17.4 | **WORKS** | Not the C extension: IronRuby implements ffi on its own Fiddle. ffi's own RSpec suite runs 5009 examples with 656 failures, all in four documented limits (struct by value across a call, float variadics, :long_double, dispatcher thread names) |
| 29 | rspec (with -core, -expectations, -mocks, -support) | 3.13.2 | **WORKS** | a real in-process run: matchers, doubles, shared examples, and a failure, an error and a pending example, all reported identically |
| 30 | unicode-display_width | 3.3.0 | **WORKS** | East Asian wide, combining marks, emoji sequences, `overwrite:` |
| 31 | builder | 3.3.0 | **WORKS** | nested XML with attributes, namespaces, comments, escaping |
| 32 | mime-types | 3.7.0 | **WORKS** | the registry: lookup by type and by filename, extensions, `binary?`, ordering |
| 33 | multipart-post | 2.4.1 | **WORKS** | a multipart body built from a param and a file part |
| 34 | activemodel | 8.1.3.1 | **WORKS** | attributes and type casting, validations, custom validators, `Dirty`, JSON serialisation |
| 35 | mini_mime | 1.1.5 | **WORKS** | lookup by filename and by content type |
| 36 | parser | 3.3.12.0 | **WORKS** | parses a class with kwargs, pattern matching, rescue/retry and interpolation; reports a syntax error the same way |
| 37 | activerecord | 8.1.3.1 | **WORKS** | a real in-memory SQLite database through IronRuby's sqlite3: schema DSL, associations, scopes, joins, aggregates, validations, transactions and rollback |
| 38 | actionpack | 8.1.3.1 | **WORKS** | a route set with resources, URL helpers, recognition, `ActionDispatch::Request`, strong parameters |
| 40 | parallel | 2.2.0 | **WORKS** | `in_threads` map and each, exception propagation, `Parallel::Break`. `in_processes` needs fork, which the CLR has not got |
| 41 | jwt | 3.3.0 | **WORKS** | HS256/384/512, RS256 and ES256 round trips, verification, expiry and algorithm-confusion errors |
| 42 | rack-test | 2.2.0 | **WORKS** | GET/POST, cookies, redirects, custom headers |
| 43 | ast | 2.4.3 | **WORKS** | node construction, the processor mixin, `updated`, equality and hashing |
| 44 | tilt | 2.9.0 | **WORKS** | ERB and String templates, locals, scopes, `__LINE__` offsets, errors from inside a template |
| 46 | rubyzip | 3.7.0 | **WORKS** | writes a zip to a buffer and reads it back: entries, CRCs, compressed sizes, `Zip::Error` |
| 47 | railties | 8.1.3.1 | **WORKS** | a whole Rails application booted in-process, routed, and served requests through Rack::Test |
| 48 | actionview | 8.1.3.1 | **WORKS** | tag, number, text and sanitising helpers, and an ERB template compiled and rendered. Needed the top-level `include` hook fix |
| 49 | method_source | 1.1.0 | **WORKS** | source and comments for methods and procs, `SourceNotFoundError` for a C method |
| 51 | mail | 2.9.1 | **WORKS** | building, serialising and reparsing a message, multipart bodies, addresses, base64/quoted-printable |
| 52 | rainbow | 3.1.1 | **WORKS** | named colours, X11 colours, RGB and hex, modes, nesting, `enabled = false` |
| 54 | ruby-progressbar | 1.13.0 | **WORKS** | custom formats, `progress=`, finishing, `InvalidProgressError` |
| 55 | loofah | 2.25.2 | **WORKS** | `strip`, `prune`, `escape` and `whitewash` scrubbers and a custom one, on the Nokogiri shim |
| 56 | rubocop | 1.91.0 | **WORKS** | `rubocop --format emacs` over a file with a real offence, plus `ConfigLoader` and `ProcessedSource`. Needed the character-class fixes, the YAML folded-scalar fix and `Psych::TreeBuilder` |
| 59 | connection_pool | 3.0.2 | **WORKS** | six threads through a pool of three, `TimeoutError`, `Wrapper`, shutdown |
| 60 | excon | 1.7.1 | **WORKS** | stubbed requests, `expects:`, `StubNotFound`, the query-string and redaction utilities |
| 61 | rails-html-sanitizer | 1.7.1 | load only | exercised through loofah |
| 62 | activejob | 8.1.3.1 | **WORKS** | the test adapter, queues, callbacks, `perform_now`, serialisation and deserialisation |
| 63 | zeitwerk | 2.8.3 | **WORKS** | autoloading a real directory tree, a custom inflection, `on_load`, eager loading, unloading |
| 64 | nio4r | 2.7.5 | **IMPOSSIBLE as a gem** | C extension: an epoll/kqueue selector. A .NET shim is plausible - see below |
| 65 | http-cookie | 1.1.6 | **WORKS** | a cookie jar: parsing Set-Cookie, domain and path matching, `HttpOnly`, expiry |
| 66 | rails-dom-testing | 2.3.0 | load only | exercised through actionpack |
| 67 | domain_name | 0.6.20260907 | **WORKS** | registrable domain, cookie-domain rules, IDN, superdomains |
| 68 | regexp_parser | 2.13.0 | **WORKS** | parses and walks a pattern with groups, quantifiers and alternation; scanner and lexer |
| 69 | globalid | 1.4.0 | **WORKS** | GID round trip, `Locator`, parsing. Signed GIDs are not exercised: they need a `JSON.parse` arity the oracle's json gem refuses |
| 70 | websocket-driver | 0.8.2 | **BROKEN** | C extension (the mask loop). The gem will not install without it, although its Ruby is written to run without the `.so` |
| 71 | coderay | 1.1.3 | **WORKS** | scans Ruby, JSON and SQL; token streams, HTML and terminal encoders. Needed the nested-character-class fix |
| 72 | pry | 0.16.0 | **WORKS** | a real REPL session driven from a string: evaluation, `show-source`, `cd`/`_`, and `Pry::Method` |
| 73 | erubi | 1.13.1 | **WORKS** | compiles and evaluates templates, escaping, trim mode, custom bufvar |
| 74 | sprockets | 4.4.1 | **WORKS** | an asset environment: `//= require` directives, concatenation, content types |
| 76 | crass | 1.0.7 | **WORKS** | parses a stylesheet with comments, at-rules and declarations; tokenizer and stringifier |
| 77 | mini_portile2 | 2.8.9 | load only | a build helper for C extensions; there is nothing else to ask of it here |
| 78 | thread_safe | 0.3.6 | load only | a deprecated shim over concurrent-ruby |
| 79 | puma | 8.0.2 | **IMPOSSIBLE as a gem** | C extension: the HTTP parser and the IO reactor |
| 81 | multi_xml | 0.9.1 | **WORKS** | parses a document with attributes, typed values and nested elements; `ParseError`. Needed the `REXML::Attributes#each_attribute` enumerator fix |
| 82 | websocket-extensions | 0.1.5 | **WORKS** | the extension negotiation protocol with a hand-written extension, plus the header parser |
| 83 | redis | 6.0.0 | **WORKS** (no server) | URL and config parsing, the error hierarchy, and a connection attempt that fails as `Redis::CannotConnectError`. There is no redis-server here to talk to |
| 84 | rubocop-ast | 1.50.0 | **WORKS** | node traversal, `NodePattern` matching, tokens, method and argument nodes |
| 86 | netrc | 0.11.0 | **WORKS** | reads, writes and re-reads a .netrc, permission check included |
| 87 | dotenv | 3.2.0 | **WORKS** | quoting, `export`, interpolation, comments, `load` vs `overload` |
| 90 | httpclient | 2.9.0 | **WORKS** | real requests over a loopback WEBrick: GET, POST, redirect following, status handling. Needed the `!~` fix |
| 92 | msgpack | 1.8.5 | **IMPOSSIBLE as a gem** | C extension, no pure-Ruby fallback |
| 93 | rack-protection | 4.2.1 | **WORKS** | XSS, frame-options, path-traversal, IP-spoofing and origin middleware, each with a request that trips it |
| 94 | rb-fsevent | 0.11.2 | load only | macOS only; inert here, exactly as on Linux CRuby |
| 95 | googleauth | 1.17.4 | load only | every path through it needs Google credentials or the metadata server |
| 96 | unf | 0.2.0 | load only | the pure-Ruby normaliser, because unf_ext is a C++ extension |
| 98 | docile | 1.4.1 | **WORKS** | DSL evaluation against an object, an Array, immutable chaining, block return |
| 99 | simplecov | 1.3.0 | **WORKS** | source files, filters, groups and results over a hand-made coverage map. Measuring real coverage needs `Coverage`, which is a separate question |
| 100 | signet | 0.22.0 | load only | an OAuth2 client; every path through it needs a server |

Named in the brief, further down the list:

| Gem | Version | Status | Evidence / reason |
|-----|---------|--------|-------------------|
| sinatra | 4.2.1 | **WORKS** | routes, params, splats, redirects, custom 404 and 500 handlers, content types. Needed the `Regexp`-is-not-`Enumerable` fix |
| roda | 3.108.0 | **WORKS** | the routing tree, the json and halt plugins, the error handler |
| sqlite3 | IronRuby's own | **WORKS** | DDL, bind parameters (positional, array and named), prepared statements, blobs, transactions, both error classes. The C-extension gem does not install |
| sidekiq | 8.1.7 | **WORKS** | jobs, queues, options, `Sidekiq::Testing` fake/inline/drain |
| webmock | 3.26.4 | **WORKS** | stubbing Net::HTTP by URI, body and headers, regexp matching, `NetConnectNotAllowedError` |
| vcr | 6.4.0 | **WORKS** | replays a hand-written cassette through webmock, and refuses an unrecorded request |
| kramdown | 2.5.2 | **WORKS** | a document with lists, tables, footnotes, definition lists and code blocks, to HTML, LaTeX and back to kramdown. Needed the `[^^]` fix |
| asciidoctor | 2.0.26 | **WORKS** | sections, lists, a table, an admonition, a source block and a cross reference, plus the document model |
| terminal-table | 4.0.0 | **WORKS** | titles, headings, separators, alignment, colspan, non-ASCII cells. Needed the `String#ljust` character-count fix |
| colorize | 1.1.0 | **WORKS** | colours, modes, nesting, `uncolorize`, `disable_colorization` |
| hashie | 5.1.0 | **WORKS** | Mash, Dash, Trash, coercion, deep merge, indifferent access. Needed removing IronRuby's top-level `::Config` |
| dry-inflector | 1.3.1 | **WORKS** | the full inflection set with custom rules, acronyms and uncountables |
| dry-configurable | 1.4.0 | **WORKS** | class and instance settings, nested settings, constructors, `finalize!` |
| tty-prompt | 0.23.1 | **WORKS** | `ask`, `yes?`, `select`, `multi_select` and `mask`, driven through `TTY::Prompt::Test` |
| octokit | 10.0.0 | **WORKS** | repository naming, the URL builders, the error mapping from a response, Sawyer resources |
| sawyer | 0.9.3 | **WORKS** | an agent over Faraday's test adapter: resources, nested resources, hypermedia relations |
| listen | 3.10.0 | **WORKS** | watches a real directory with the polling adapter and sees a file appear |
| chunky_png | 1.4.0 | **WORKS** | draws into an image, encodes it, decodes it again and compares the pixels |
| standard | 1.56.0 | **WORKS** | `Standard::Cli` over a file with a real offence; it is RuboCop underneath |
| yard | 0.9.45 | **BROKEN** | `require "yard"` succeeds; parsing Ruby does not. `YARD::Parser::Ruby::RipperParser` builds its handlers from `Ripper::PARSER_EVENT_TABLE` at load, and IronRuby's Ripper is a prism tree translator with `Ripper.sexp` and the scanner events, not an event parser |
| prawn | 2.5.0 | **WORKS** | two A4 pages with text, shapes, a text box and page numbers, rendered to the same bytes. ttfunk pins `bigdecimal ~> 3.1`; IronRuby's built-in bigdecimal is 4.0.1, and RubyGems now reads a 2.x or 3.x requirement on it as `>=` (`rubygems/defaults/ironruby.rb`) |
| rb-inotify | 0.11.1 | **WORKS** | on IronRuby's ffi; create/modify/delete events match CRuby |
| bcrypt | 3.1.22 | **IMPOSSIBLE as a gem** | C extension: the Blowfish KDF. A port, not a shim - `BCrypt.Net` already exists on NuGet |
| oj | 3.17.6 | **IMPOSSIBLE as a gem** | C extension. Use `json`, which IronRuby implements |
| google-protobuf | 4.36.2 | **IMPOSSIBLE as a gem** | C extension over upb |
| pg | 1.6.3 | **IMPOSSIBLE as a gem** | C extension over libpq. Npgsql is the .NET answer and would be a new adapter, not a shim |
| mysql2 | 0.5.7 | **IMPOSSIBLE as a gem** | C extension over libmysqlclient |
| sassc | 2.4.0 | **IMPOSSIBLE as a gem** | C++ extension over libsass, itself deprecated upstream |

## The 23 top-100 gems not in the popular tier

* Already in the **stdlib tier** of the same catalog, because CRuby ships them:
  `rake` (8), `json` (9), `minitest` (14), `rexml` (39), `racc` (58),
  `base64` (91), `bigdecimal` (97).
* **The tooling doing the installing**: `bundler` (1), `rubygems-update` (75).
  IronRuby's RubyGems is what installed everything in the table.
* **Exercised through the gem that owns them**: `rspec-core` (20),
  `rspec-expectations` (21), `rspec-mocks` (22), `rspec-support` (23) through
  rspec; `mime-types-data` (57) through mime-types; `faraday-net_http` (53)
  through faraday.
* **Rails components with no exercise of their own**: `rails` (45),
  `actionmailer` (50), `actioncable` (80), `activestorage` (88),
  `sprockets-rails` (85). railties boots a whole application and actionpack,
  actionview, activerecord, activejob, globalid and sprockets are each
  exercised; these five are not, so they are not claimed.
* **AWS service gems over aws-sdk-core**: `aws-sdk-s3` (26), `aws-sdk-kms` (28).
* `unf_ext` (89): a C++ extension. `unf` falls back to pure Ruby without it.

## How far down it gets

Of the 77 top-100 gems in the popular tier, **73 work** and 4 do not:
`nio4r` (64), `websocket-driver` (70), `puma` (79) and `msgpack` (92). All four
are the same thing - a C extension with no pure-Ruby path - and the first of
them is at **#64**.

This table was measured before ffi existed here. ffi was #27 and the first
stop; it is implemented now (on Fiddle, see `Util/ffi-matrix.rb`), which moves
the first stop down to #64 and brings rb-inotify, ethon, typhoeus and rbnacl
with it.

Nothing in the top 100 fails any more for a reason that is IronRuby's own. The
ones that did are fixed: Thor, multi_json, Sinatra, RuboCop, terminal-table,
multi_xml, hashie, coderay, kramdown, aws-sdk-core, httpclient, actionview and
nokogiri. What they needed is in the four commits this work produced, and
`ironruby/gem_compat.rb` did not have to grow: every one of them was a bug in
IronRuby, not a gem that needed patching.

## The shims that would unblock the most

1. ~~**ffi** (#27)~~ - **done**, in the same round as this survey. It was
   written the way this entry proposed: on Fiddle rather than beside it, so
   there is one thunk cache, one closure emitter and one ownership model.
   `attach_function` reaches `NativeLibrary.GetExport`, and calls go through
   Fiddle's `DynamicMethod`+`EmitCalli` thunks. rb-inotify, ethon, typhoeus and
   rbnacl were verified against CRuby on top of it.
2. **nio4r** (#64, and therefore ActionCable, Puma's reactor and anything built
   on a selector). `NIO::Selector` is `Socket.Select` or, better,
   `SocketAsyncEventArgs`; `NIO::Monitor` is a small object around it. Far
   smaller than ffi and entirely doable in C# against `System.Net.Sockets`.
3. **websocket-driver** (#70). Its C extension is *only* the XOR mask loop; the
   protocol is Ruby and the gem is written to run without the `.so`. It simply
   refuses to install when the extension fails to build. Either a tiny C#
   masking method under the same name, or making RubyGems treat a failed
   optional extension as non-fatal, unblocks it.
4. **msgpack** (#92) and **oj**. Both are pure serialisation with no OS surface,
   so both are straightforward C# libraries in the mould of
   `Src/Libraries/Json` - msgpack especially, whose format is a page of spec.
5. **Ripper's event parser**, for YARD. IronRuby already has prism's tree and
   token stream; what is missing is `PARSER_EVENT_TABLE` and the dispatch that
   calls `on_<event>` while walking it. That is Ruby, in
   `Src/StdLib/ironruby/ripper.rb`, and it would serve anything else that
   subclasses `Ripper`.
6. **`Coverage`**, so SimpleCov measures something instead of being exercised on
   a hand-made map. That needs interpreter support, not a shim.

## Things that are not shims

`pg`, `mysql2` and `sassc` are C bindings to libraries the CLR reaches through
entirely different .NET packages (Npgsql, MySqlConnector, LibSassHost). The
honest route is an adapter written against those - which is exactly what
`Src/Libraries/Sqlite3` already is for sqlite3, and why `activerecord` is in the
WORKS column with a real database behind it.

`bcrypt` and `google-protobuf` are ports rather than shims: real algorithms and
real wire formats with good .NET implementations already, so the work is
plumbing a Ruby API onto them.

## Known gaps behind a WORKS

* The Nokogiri shim does not record parse errors for malformed XML, so
  `doc.errors` is empty where libxml2's would not be.
* Nokogiri's SAX events are replayed off the parsed tree rather than streamed,
  so a document has to fit in memory - which is the one thing SAX exists to
  avoid.
