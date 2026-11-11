#!/bin/sh
# The short name for ./ironruby, which it runs with the same arguments.
# Found by path and not a symlink: exec its neighbour without the readlink fork.
case "$0" in
  */*) [ -L "$0" ] || exec "${0%/*}/ironruby" "$@" ;;
esac
exec "$(dirname "$(readlink -f "$0")")/ironruby" "$@"
