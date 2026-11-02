@echo off
rem IronRuby Notepad. Double-click it, drop a file on it, or run: notepad.cmd file.txt
rem
rem ironrubyw.exe is ironruby without a console window, as rubyw.exe is ruby's: a Windows
rem Forms program has no use for one. To keep a console for Ruby's output, run
rem ..\..\ironruby.cmd notepad.rb instead. The build is chosen as ironruby.cmd chooses it.
rem
rem A build from before ironrubyw has only ir.exe, a console program, which Windows gives
rem a console window: "start /b" leaves that console to ir.exe alone once this batch file
rem has ended, and notepad.rb then lets go of it, which closes the window. (Single-line
rem ifs: a dropped file name with a ")" in it would end a parenthesized block.)
setlocal
set "IR_ROOT=%~dp0..\.."
if "%IR_CONFIG%"=="" if not exist "%IR_ROOT%\Src\Console\bin\Debug\" if exist "%IR_ROOT%\Src\Console\bin\Release\" set "IR_CONFIG=Release"
if "%IR_CONFIG%"=="" set "IR_CONFIG=Debug"
if "%IR_TFM%"=="" set "IR_TFM=net8.0"
set "IR_DIR=%IR_ROOT%\Src\Console\bin\%IR_CONFIG%\%IR_TFM%\win-x64"
if not exist "%IR_DIR%\ir.exe" set "IR_DIR=%IR_ROOT%\Src\Console\bin\%IR_CONFIG%\%IR_TFM%"
set "S=%IR_ROOT%\Src\StdLib"
if exist "%IR_DIR%\ironrubyw.exe" start "" "%IR_DIR%\ironrubyw.exe" "-X:StdLib=%S%\ironruby;%S%\ruby\4.0" "%~dp0notepad.rb" %*
if not exist "%IR_DIR%\ironrubyw.exe" start "" /b "%IR_DIR%\ir.exe" "-X:StdLib=%S%\ironruby;%S%\ruby\4.0" "%~dp0notepad.rb" %*
