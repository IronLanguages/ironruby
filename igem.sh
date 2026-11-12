#!/bin/sh
# IronRuby's gem.  The same thing as `./ironruby -S gem`, under the i-prefixed name
# IronRuby has used since 1.x -- the point of the prefix is co-installation: when
# CRuby is also on PATH, plain `gem` is ambiguous.  (JRuby ships jgem for the same
# reason, alongside its unprefixed bin/gem.)
IR_ROOT="$(dirname "$(readlink -f "$0")")"
exec "$IR_ROOT/ironruby" -S gem "$@"
