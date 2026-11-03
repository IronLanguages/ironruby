# frozen_string_literal: true
#
# The strptime gem's C extension, provided by IronRuby itself.
#
# strptime 0.2.5 (the fast strptime/strftime engine fluentd parses its time
# formats with) is a little Ruby over one C extension, strptime/strptime, which
# defines the classes Strptime and Strftime.  Here they are C#
# (Src/Libraries/Strptime), a port of ext/strptime.
#
# Only the extension is provided.  The gem itself is installed as usual - `gem
# install strptime` and `bundle install` skip compiling its extension on
# IronRuby (see rubygems/defaults/ironruby.rb) - and its lib/strptime.rb, which
# requires "strptime/strptime", finds this file: a .rb anywhere on $LOAD_PATH
# wins over the gem's own strptime.so, which IronRuby could not load anyway.

#
# Both classes are opened here first, as plain Ruby classes: the gem's
# strptime/version.rb has usually defined Strptime already, and a class that
# exists cannot be given a CLR allocator afterwards, so the C# methods work on
# ordinary objects either way and this makes the two cases one.

class Strptime
end

class Strftime
end

load_assembly 'IronRuby.Libraries', 'IronRuby.StandardLibrary.Strptime'
