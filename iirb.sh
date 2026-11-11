#!/bin/sh
# IronRuby's irb, the same as `./ironruby -S irb`, under the i-prefixed name
# (IronRuby 1.x shipped iirb.bat; JRuby ships jirb), so that `irb` stays CRuby's.
IR_ROOT="$(dirname "$(readlink -f "$0")")"
exec "$IR_ROOT/ironruby" -S irb "$@"
