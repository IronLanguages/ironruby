#!/bin/sh
# IronRuby's ahead-of-time compiler, the analogue of JRuby's jrubyc.  The same
# thing as `./ironruby -S irubyc`, under the i-prefixed name IronRuby has used
# since 1.x (see igem.sh for why the prefix is there).
#
#   ./irubyc.sh -t /tmp/out app.rb lib/
#   /tmp/out/app/app
#
# It needs the .NET SDK: it generates a C# host, compiles it, and embeds the
# Ruby sources in the resulting assembly.  The Ruby itself is still compiled by
# IronRuby at startup -- irubyc does not emit IL for it.  See --help.
IR_ROOT="$(dirname "$(readlink -f "$0")")"
exec "$IR_ROOT/ironruby" -S irubyc "$@"
