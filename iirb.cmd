@echo off
rem IronRuby's irb on Windows, the same as `ironruby.cmd -S irb`. i-prefixed, so that
rem `irb` on the PATH stays CRuby's even with IronRuby's directory in front of it.
"%~dp0ironruby.cmd" -S irb %*
