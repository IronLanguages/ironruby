@echo off
rem Runs the built ir with the prism front end and the bundled standard library.
rem The Windows twin of ironruby; see it for why each switch is here, and for the name.
rem ir.cmd is the short name for it.
rem
rem   ironruby.cmd -e "puts RUBY_DESCRIPTION"
rem   set RUBY_EXE=%CD%\ironruby.cmd
rem
setlocal
set "IR_ROOT=%~dp0"
if "%IR_ROOT:~-1%"=="\" set "IR_ROOT=%IR_ROOT:~0,-1%"

rem IR_CONFIG=Release runs the optimized build instead. Debug stays the default,
rem exactly as in ironruby.
rem A binary release (Util/package-release.sh) ships only the Release build: when
rem IR_CONFIG is not set and there is no Debug build, run that one.
if "%IR_CONFIG%"=="" if not exist "%IR_ROOT%\Src\Console\bin\Debug\" if exist "%IR_ROOT%\Src\Console\bin\Release\" set "IR_CONFIG=Release"
if "%IR_CONFIG%"=="" set "IR_CONFIG=Debug"

rem IR_TFM picks the target framework out of the multi-targeted build (net8.0;net10.0),
rem exactly as in ironruby. net8.0 stays the default.
if "%IR_TFM%"=="" set "IR_TFM=net8.0"

rem A tree built on Windows puts ir.exe straight in %IR_TFM%; one cross-published from
rem Linux with -r win-x64 puts it in %IR_TFM%\win-x64. Take whichever exists.
set "IR_BIN=%IR_ROOT%\Src\Console\bin\%IR_CONFIG%\%IR_TFM%\win-x64\ir.exe"
if not exist "%IR_BIN%" set "IR_BIN=%IR_ROOT%\Src\Console\bin\%IR_CONFIG%\%IR_TFM%\ir.exe"
rem The path is quoted below: a ")" in it, as in "Program Files (x86)", would end the block.
if not exist "%IR_BIN%" (
  echo ironruby.cmd: no %IR_CONFIG% build at "%IR_BIN%" 1>&2
  echo ironruby.cmd: build it with: dotnet build Src\Console\Ruby.Console.csproj -c %IR_CONFIG% -r win-x64 --self-contained false 1>&2
  exit /b 127
)

set "S=%IR_ROOT%\Src\StdLib"

rem -S searches RUBYPATH before PATH, so `ironruby.cmd -S irb` runs the irb shipped here.
rem The directory ir.exe is in comes next: it is RubyGems' bindir (RbConfig's bindir),
rem where `igem install` writes the binstubs of installed gems, so `ironruby.cmd -S <gem
rem executable>` finds the one written for IronRuby before a host Ruby's - as in ironruby.
for %%I in ("%IR_BIN%") do set "IR_BINDIR=%%~dpI"
set "IR_BINDIR=%IR_BINDIR:~0,-1%"
if "%RUBYPATH%"=="" (set "RUBYPATH=%S%\bin;%IR_BINDIR%") else (set "RUBYPATH=%S%\bin;%IR_BINDIR%;%RUBYPATH%")

rem -X:StdLib is split on Path.PathSeparator, which is ';' on Windows - a ':' list would
rem be cut apart at every drive letter.
"%IR_BIN%" "-X:StdLib=%S%\ironruby;%S%\ruby\4.0" %*
exit /b %ERRORLEVEL%
