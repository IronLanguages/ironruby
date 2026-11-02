# IronRuby.Prism — CRuby's parser as an IronRuby front end

Binding of [ruby/prism](https://github.com/ruby/prism), the parser CRuby
uses, via P/Invoke to `libprism.so`. It is IronRuby's only parser: it replaced the
hand-ported 1.9-era `Tokenizer.cs` + gppg `Parser.y` front end, which has been
removed, with the same parser CRuby, JRuby and TruffleRuby use, targeting current
(4.x-level) Ruby syntax. Of the old `Tokenizer` only its static helpers remain - the
rules for Ruby names, number literals and the encoding magic comment, which the
runtime asks about at run time.

Architecture (the JRuby approach):

1. `generate.rb` reads prism's `config.yml` and emits
   `Generated/PrismNodes.Generated.cs` (152 typed node classes + flag
   consts) and `Generated/PrismLoader.Generated.cs` (the per-node switch
   of the binary deserializer), plus `Generated/PrismMeta.Generated.cs`
   (the serialized token-type table and per-node field names, used by
   consumers that walk the tree generically - see `Src/Libraries/Ripper`).
   Re-run it when upgrading prism; the loader validates the serialization
   version at runtime.
2. `PrismLoader.cs` implements the primitives of prism's binary
   serialization (varuint/varsint, constant pool, integers, locations)
   per prism's `docs/serialization.md`. prism writes the tree in
   post-order - a node's children come before its own record - so the
   loader builds it bottom-up on an explicit stack, and no tree is too
   deep to load. A `DefNode`'s children are preceded by a marker and the
   byte lengths of its subtrees: with lazy method bodies on (every file
   but eval'd code) the body is skimmed for what the bridge has to see
   at parse time and left in the buffer until the method is first
   called.
3. `PrismParser.cs` calls `pm_serialize_parse`, passing filepath, line
   and outer-scope locals (for `eval`) through the serialized
   `pm_options_t` data blob.
4. `PrismAstBridge.cs` maps the typed nodes onto `IronRuby.Compiler.Ast`.
   Modern syntax with no 1.9 AST equivalent is lowered: safe navigation
   becomes a nil-guarded temp, keyword arguments become a trailing
   optional hash plus a prologue (missing-keyword checks, defaults,
   `**rest` extraction), `it`/numbered params become explicit block
   params, `**` splats in literals/calls become `Hash#merge` chains,
   `2r`/`2i` become `Rational`/`Complex` calls.
5. Syntax errors from prism are reported through the DLR `ErrorSink`.
6. `PrismLex.cs` decodes `pm_serialize_lex` - prism's token stream with
   the MRI lexer state each token left behind. Ripper is built on it
   (`Src/Libraries/Ripper/RipperOps.cs`, `Src/StdLib/ironruby/ripper.rb`).

Note the assembly reference runs `IronRuby.Libraries` -> `IronRuby.Prism`,
not the other way round, so that the Ripper library can call in here.

## Status

- **Prism parses everything**: the main program, `require`d files and
  `eval` (outer eval locals are passed to prism as a scope via the
  options blob). `-X:UsePrism`, which once chose it over the legacy
  parser, is still accepted and does nothing.
- **Modern syntax runs**: keyword arguments (required/optional/`**rest`,
  correct missing-keyword errors), safe navigation, endless methods,
  hash shorthand `{x:}`, `it` and numbered block params, lambdas
  (`->`, `.()`), `**` splats — verified byte-identical against CRuby 3.3
  output (and `it` runs here even though CRuby 3.3 rejects it).
- **Deep nesting runs** as deep as prism accepts it (10000 levels; a call
  chain like `a.b.c...` has no limit at all), on any thread, the 1 MB main
  thread on Windows included. prism's parser is recursive C needing about
  half a KB of native stack per level, so every source longer than 128
  bytes is parsed on a long-lived thread with a 64 MB stack
  (`PrismNative.WithParserStack`). The bridge, IronRuby's AST
  transformation and the DLR's interpreter compiler and compiled-code
  rewriter recurse per level, so each checks the stack and, when it is
  nearly used up, carries on on a fresh thread (`IronRuby.Runtime.StackGuard`,
  the DLR patch `Build/dlr-patches/0101-stack-guard.patch`); IronRuby's own
  JIT leaves such a method interpreted. Nesting deeper than prism allows is
  a "nesting too deep" SyntaxError, as in CRuby. What remains bounded by the
  stack is running code whose calls nest as deeply (a block that calls a
  block that calls a block...), and forced IL compilation
  (`-X:CompilationThreshold 0`, `-X:NoAdaptiveCompilation`) of trees
  thousands of levels deep, which reaches limits of IL and the JIT itself.
- **100% of the then-bundled 1.9 stdlib mapped through the bridge**
  (571/571 files in the former `ruby/1.9.1`, 39/39 in `ironruby/`; measure with
  `IronRuby.Prism --sweep <dir>`). Coverage includes rescue/ensure/retry,
  case/when, regexps (incl. named-capture writes), multiple/attribute/
  index assignment and op-assigns, for loops, alias/undef, singleton
  classes, BEGIN-less END blocks, string/symbol/xstring interpolation.
- Stress programs (classes, super, blocks, splat, yield, ERB templating
  via eval, OpenStruct, Time.parse, StringIO) produce byte-identical
  output vs the legacy parser. Unmapped node types (pattern matching,
  safe navigation, keyword args) raise a clean NotSupportedException
  naming the prism node.
- `PrismParser.ParseSerialized(source)` — prism's compact binary AST
  (the format JRuby/TruffleRuby load; `docs/serialization.md` in prism).
- `PrismParser.ParseToJson(source)` — full AST as JSON (what the bridge
  currently consumes).
- `dotnet run -- --json file.rb` dumps the JSON AST.
- **`require "prism"` works too.** prism's own Ruby library is a decoder:
  every entry point calls one of libprism's `pm_serialize_*` functions and
  lets `Prism::Serialize` (pure Ruby) turn the bytes into nodes.  Upstream
  gets those bytes from a CRuby C extension, or from the `ffi` gem on other
  implementations - and `ffi` is itself a C extension, so that path was
  closed here.  `PrismSerialize.cs` + `Src/Libraries/Prism/PrismOps.cs`
  expose the same serialization API to Ruby, and
  `Src/StdLib/ironruby/prism/ironruby.rb` is the third backend; the
  vendored `Src/StdLib/ruby/4.0/prism` tree is the Ruby half of the very
  prism revision `libprism` is built from, generated files included.
  `Prism::VERSION` is read from `pm_version`, so the two cannot disagree.

Build the native library first:

```sh
git clone https://github.com/ruby/prism ../../../prism
cd ../../../prism && git checkout 30a25553
ruby templates/template.rb && make shared        # -> build/libprism.so
```

**The prism commit is pinned, and it matters.** `Generated/` is generated
from a particular `config.yml`; a `libprism` built from a different one
serializes its nodes differently and the loader walks off the end of the
buffer - the symptom is a mid-parse `ArgumentError: Offset and length were
out of bounds` or `TypeError: Unknown prism node type: 0` in whichever
stdlib file trips over the changed node first, not a clean version error.
The tree as it stands matches prism
[`30a25553`](https://github.com/ruby/prism/commit/30a255539e81842b2b7f12b4fd3c62a7b4d64165),
v2.0.0 plus the commits up to 2026-10-24. Two changes on the way there
altered the serialized layout rather than just `config.yml`, so
`generate.rb` and `PrismLoader.cs` had to follow by hand: the tree is
serialized in post-order with lazy-subtree headers on `DefNode` (ruby/prism
`ab92f0eb4`), and the header carries an 8-byte checksum of the source after
the start line (`01c1960e8`). The earlier `attach_comments` change (a byte
in the options blob, a comment-attachment list in the header) is still
there too. `PRISM_REF` in `ci.yml` and `release.yml` names the same commit.
To check a checkout instead of guessing, regenerate into a scratch
directory and diff - identical output means the shared library from that
checkout is safe:

```sh
ruby generate.rb ../../../prism/config.yml /tmp/gen && diff -r Generated /tmp/gen
```

On **Windows**, build the same checkout with the RubyInstaller DevKit's
MinGW toolchain. `make shared` names its output after RbConfig's `SOEXT`,
so there it produces `build/libprism.dll`:

```
ruby templates\template.rb
ridk exec make shared -j4
```

`IronRuby.Prism.csproj` copies whichever of `libprism.so` /
`libprism.dll` / `libprism.dylib` is present next to the host, and
`PrismNative`'s `DllImport` resolver picks the one for the running
platform - so a tree holding both can publish for either.

## Known limits / next steps

- Pattern matching is lowered to ===/deconstruct tests with bindings:
  case/in with guards, captures, array/hash/const patterns, find
  patterns (`[*, x, *]` via the __pm_find_index__ prelude helper),
  `{**nil}` exact matches, pins, alternations, standalone `=>` and `in`.
  The NoMatchingPatternError message is shorter than MRI's.
- IronRuby.Tests runs every test on prism. The tests that only held for
  the 1.9 grammar (retry/redo/break outside rescue, now a SyntaxError)
  were removed when the legacy parser was; the rest expect what CRuby 4
  does, warning counts included (the suite runs with warnings on, so a
  test expects the parse warnings `ruby -wc` gives).
- `...` argument forwarding is lowered to `*?fwd?, &?fwdblk?` (keywords
  ride along as the trailing hash, consistent with the kwargs lowering).
- Keyword-argument lowering is restricted to signatures without optional
  positionals or `*rest` (where "trailing hash" and "keywords"
  coincide); unknown-keyword errors are not raised (permissive).
- String literals round-trip through UTF-16; binary string literals with
  invalid UTF-8 may lose bytes (needs byte[]-based MutableString
  literals).

Line/column info: prism gives byte offsets + a line-offset table
(`pm_parser_line_offsets`); IronRuby's `SourceSpan`s are built from the
same data, so source maps carry over directly.
