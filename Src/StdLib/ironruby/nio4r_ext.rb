# frozen_string_literal: true
#
# nio4r's native half, provided by IronRuby itself.
#
# `gem install nio4r` cannot work here: the gem is a C extension over libev.  What IronRuby ships
# instead is the gem's own Ruby half (Src/StdLib/ironruby/nio.rb and nio/, vendored from nio4r
# 2.7.5 - nio.rb with a two-line IronRuby branch next to its JRuby one) on top of a C#
# implementation of ext/nio4r: NIO::Selector, NIO::Monitor and NIO::ByteBuffer in
# Src/Libraries/Nio4r/Nio4r.cs.  NIO::ENGINE is "dotnet"; NIO4R_PURE=true still selects the gem's
# pure-Ruby engine (lib/nio/selector.rb, on IO.select).
#
# Why a C# selector rather than the pure-Ruby one, which also passes nio4r's suite here: the
# pure-Ruby selector rebuilds its reader and writer arrays and calls IO.select on every #select,
# and IO.select asks each IO about itself, so every call costs O(registered IOs) in Ruby as well
# as in the kernel.  The C# selector keeps the interest set, and on Linux hands it to epoll(7),
# so the kernel's part of a #select costs O(ready); elsewhere it is one poll(2) (Unix) or one
# Socket.Select (Windows) per call.  NIO::Selector.backends answers [:epoll, :poll] on Linux,
# [:poll] on other Unixes and [:select] on Windows - libev's names for the same mechanisms.
#
# Measured on a loaded 8-core box (load average ~30), Release build, one busy connection
# ping-ponging through the selector with N idle ones registered, microseconds per round trip,
# and one select(0) over 1000 idle sockets, CPU microseconds per call:
#
#                           N=1    N=100   N=1000   select(0), N=1000
#   CRuby  libev (epoll)    233      39       42        2
#   CRuby  pure Ruby        122     167     2393      762
#   IronRuby pure Ruby     1828    1530     6434     3790
#   IronRuby dotnet/poll    324     805     2630     1030
#   IronRuby dotnet/epoll   598     579     1210      290
#
# (The N=1 column is mostly the box: IronRuby's own socket round trip is ~300us here.)  What
# the table says: the C# selector is 2-5x the pure-Ruby one at scale, and epoll is what keeps
# a reactor full of idle keep-alive connections - puma's - from paying for all of them on
# every wakeup.
#
# A default gemspec (Src/StdLib/ruby/gems/4.0.0/specifications/default/nio4r-2.7.5.gemspec) makes
# RubyGems agree that nio4r is installed, so `gem "nio4r"` - and puma, and actioncable - resolve
# against this rather than against rubygems.org.

load_assembly 'IronRuby.Libraries', 'IronRuby.StandardLibrary.Nio4r'
