@echo off
rem The short name for ironruby.cmd, which it runs with the same arguments. Not `call`:
rem that would double every ^ in them.
"%~dp0ironruby.cmd" %*
