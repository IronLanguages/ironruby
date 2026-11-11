# frozen_string_literal: true
#
# cool.io's C extension, provided by IronRuby itself.
#
# cool.io (the event loop fluentd's inputs, outputs and timers run on) is Ruby over one C
# extension, cool.io_ext: Coolio::Loop, the Watcher classes, Coolio::Buffer and Coolio::Utils
# on libev.  `gem install cool.io` works here - IronRuby never compiles an extension
# (rubygems/defaults/ironruby.rb) - and installs the gem's Ruby half; its lib/cool.io.rb ends up
# at `cool_require "cool.io_ext"`, and this is the file that require finds: the extension's
# classes written in C# (Src/Libraries/CoolIo), with the gem's own Ruby loading on top of
# them, as a JRuby -java gem's does.  Nothing of the gem is vendored and no version is pinned:
# what is provided is the extension's interface, checked against the specs of cool.io 1.9.1
# (what fluentd's lockfiles pin) and 1.11.0 (what `gem install cool.io` picks today).
#
# A .so the host CRuby built next to the gem's Ruby (lib/cool.io_ext.so) is never loaded
# instead: a bare require prefers a .rb anywhere on $LOAD_PATH, as MRI's does.

load_assembly 'IronRuby.Libraries', 'IronRuby.StandardLibrary.CoolIo'

# rb_struct_define("StatInfo", ...) in stat_watcher.c: what a StatWatcher's #on_change is
# handed, twice.  "guid" (sic) is the extension's spelling.
unless defined?(Struct::StatInfo)
  Struct.new("StatInfo", :mtime, :ctime, :atime, :dev, :ino, :mode, :nlink, :uid, :guid,
             :rdev, :size, :blksize, :blocks)
end
