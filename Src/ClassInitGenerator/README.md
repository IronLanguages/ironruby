# ClassInitGenerator

IronRuby does **not** discover `[RubyMethod]` / `[RubyModule]` / `[RubyClass]` /
`[RubyConstant]` by reflecting at runtime. This tool reflects over the built
library assemblies and emits the registration code that the runtime actually
executes. If you add a new `[RubyMethod]` and do not re-run this generator, the
method is simply never registered.

The project is now SDK-style and targets `net8.0`.

## Build

```bash
export DOTNET_ROOT=/usr/local/dotnet
export PATH=/usr/local/dotnet:$PATH

dotnet build Src/ClassInitGenerator/ClassInitGenerator.csproj
```

Output lands in `Src/ClassInitGenerator/bin/Debug/net8.0/`. Because the project
references `Ruby.csproj`, `IronRuby.Libraries.csproj` and
`IronRuby.Libraries.Yaml.csproj`, that directory also contains freshly built
copies of `IronRuby.dll`, `IronRuby.Libraries.dll` and
`IronRuby.Libraries.Yaml.dll` — those are the assemblies the generator reflects
over, so a plain `dotnet build` of this project is enough to pick up your new
`[RubyMethod]`.

## Regenerate `Src/Libraries/Initializers.Generated.cs`

(The modern replacement for `Src/Libraries/GenerateInitializers.cmd`.)

```bash
D=Src/ClassInitGenerator/bin/Debug/net8.0

dotnet $D/ClassInitGenerator.dll $D/IronRuby.Libraries.dll \
  "/libraries:IronRuby.Builtins;IronRuby.StandardLibrary.Threading;IronRuby.StandardLibrary.Sockets;IronRuby.StandardLibrary.OpenSsl;IronRuby.StandardLibrary.Digest;IronRuby.StandardLibrary.Zlib;IronRuby.StandardLibrary.StringIO;IronRuby.StandardLibrary.StringScanner;IronRuby.StandardLibrary.Enumerator;IronRuby.StandardLibrary.FunctionControl;IronRuby.StandardLibrary.FileControl;IronRuby.StandardLibrary.BigDecimal;IronRuby.StandardLibrary.Iconv;IronRuby.StandardLibrary.ParseTree;IronRuby.StandardLibrary.Open3;IronRuby.StandardLibrary.Win32API;IronRuby.StandardLibrary.Json;IronRuby.StandardLibrary.Date;IronRuby.StandardLibrary.Syslog;IronRuby.StandardLibrary.Coverage;IronRuby.StandardLibrary.Ripper;IronRuby.StandardLibrary.Termios;IronRuby.StandardLibrary.Pty;IronRuby.StandardLibrary.Prism;IronRuby.StandardLibrary.Sqlite3;IronRuby.StandardLibrary.Nokogiri;IronRuby.StandardLibrary.Fiddle;IronRuby.StandardLibrary.WebSocketDriver;IronRuby.StandardLibrary.MessagePack;IronRuby.StandardLibrary.Oj;IronRuby.StandardLibrary.BCrypt;IronRuby.StandardLibrary.Nio4r;IronRuby.StandardLibrary.Puma;IronRuby.StandardLibrary.CoolIo;IronRuby.StandardLibrary.Markly;IronRuby.StandardLibrary.Strptime;IronRuby.StandardLibrary.Zstd;IronRuby.StandardLibrary.Yajl;IronRuby.StandardLibrary.Rbs" \
  /out:Src/Libraries/Initializers.Generated.cs
```

Note the quotes around `/libraries:` — the list is `;`-separated and the shell
would otherwise split it.

If you add a **new library namespace**, you must add it to that list (and to the
list here in this README), otherwise nothing in it is registered.

## Regenerate `Src/Libraries.Yaml/Initializer.Generated.cs`

```bash
dotnet $D/ClassInitGenerator.dll $D/IronRuby.Libraries.Yaml.dll \
  /libraries:IronRuby.StandardLibrary.Yaml \
  /out:Src/Libraries.Yaml/Initializer.Generated.cs
```

## Regenerate `Src/Ruby/Compiler/ReflectionCache.Generated.cs`

Only needed when you add or change a method/field marked `[Emitted]` in
`Src/Ruby` (`RubyOps` and friends), *not* for `[RubyMethod]` changes.

```bash
dotnet $D/ClassInitGenerator.dll /refcache /out:Src/Ruby/Compiler/ReflectionCache.Generated.cs
```

An `[Emitted]` member that has overloads cannot be cached under its bare name.
Those are declared by hand in the other half of the partial class,
`Src/Ruby/Compiler/ReflectionCache.cs`, which names each overload
(`CreateFrozenMutableStringL` vs `CreateFrozenMutableStringLDebug`) and picks it by
signature; the generator prints a SKIP line for each and carries on. It used to treat
them as an error and exit 1 *without writing the file*, which is why the generated file
had drifted into being hand-maintained. If you add an `[Emitted]` overload and do not
add a hand-written entry, the build fails with a missing `Methods.<name>` - which is
the loud failure you want.

A type's `[Emitted]` members are only picked up if the type itself is
`[ReflectionCached]`. The output is sorted by name, so a hand-inserted entry shows up
as a reordering diff even when its text is right.

## Procedure after adding a `[RubyMethod]`

1. Add the method in `Src/Libraries/...`.
2. `dotnet build Src/ClassInitGenerator/ClassInitGenerator.csproj`
3. Run the "Regenerate `Initializers.Generated.cs`" command above.
4. Rebuild the solution and run the tests.

To check what a run *would* change without clobbering the tree, write to a
temporary path and diff (the checked-in files use CRLF):

```bash
dotnet $D/ClassInitGenerator.dll ... /out:/tmp/Initializers.Generated.cs
diff --strip-trailing-cr /tmp/Initializers.Generated.cs Src/Libraries/Initializers.Generated.cs
```

## Known differences vs. the currently checked-in generated files

A regeneration today is *not* byte-identical to what is checked in. All of the
differences were understood and are benign:

* **Method ordering.** The generator sorts names with the default
  culture-sensitive `string.CompareTo`. .NET Framework used Windows NLS
  collation; .NET 8 on Linux uses ICU, which orders punctuation differently
  (e.g. `__send__` now sorts before `!`). Registration order between distinct
  names is irrelevant. Beware that this also makes the output mildly
  platform-dependent.
* **Overload ordering.** Overloads are emitted in `Type.GetMethods()` order,
  which is unspecified and differs between .NET Framework and CoreCLR. Four
  method groups are affected (`BigDecimal#<=>`, `IO#readlines`,
  `Kernel#define_singleton_method`, `String#each_line`/`#lines`). The delegate
  list and its parameter-attribute `uint` array permute together, so the
  registered overload set is unchanged.
* **`Microsoft.Scripting.Math.BigInteger` → `System.Numerics.BigInteger`** in
  the YAML initializer: only the mangled loader-method name changes.
* **`ReflectionCache.Generated.cs`:** the generator emits `#if !CLR2` where the
  checked-in file says `#if FEATURE_CORE_DLR` (equivalent here). The checked-in
  file also contains hand-maintained entries that the generator writes
  differently — see the note below.

### Bug in the checked-in `ReflectionCache.Generated.cs`

The checked-in file has a hand-written `Methods.CreateRegexB` that is a
copy-paste of `CreateRegexL`:

```csharp
public static MethodInfo/*!*/ CreateRegexB { get { return _CreateRegexL ?? (_CreateRegexL = CallInstruction.CacheFunc<System.String, ...>(RubyOps.CreateRegexL)); } }
```

It caches `RubyOps.CreateRegexL(string, ...)` under the name `CreateRegexB`,
even though `RubyOps.CreateRegexB` takes `byte[]`. `Methods.CreateRegexB` is
used by `Src/Ruby/Compiler/Ast/Expressions/RegularExpression.cs`. Regenerating
the file fixes this. Related: the `Yield*`/`YieldSplat*`/`Create*N` entries in
the checked-in file spell `Proc` and `MutableString[]` with short names, which
the generator writes fully qualified — cosmetic, and another sign those lines
were maintained by hand.
