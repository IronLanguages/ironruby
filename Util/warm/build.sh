#!/bin/sh
# Builds the warm-start client and broker (Util/warm/README.md) into Util/warm/bin, when missing
# or older than their source. The client is linked statically: it is on the path of every
# warm `ir` and should cost no more than an exec.
W="$(dirname "$(readlink -f "$0")")"
mkdir -p "$W/bin" || exit 1
for p in ir-client ir-broker; do
  if [ ! -x "$W/bin/$p" ] || [ "$W/$p.c" -nt "$W/bin/$p" ]; then
    flags="-O2 -Wall -Wextra -Wno-unused-parameter"
    [ "$p" = ir-client ] && flags="$flags -static"
    echo "cc $flags -o bin/$p $p.c"
    ${CC:-cc} $flags -o "$W/bin/$p" "$W/$p.c" || exit 1
  fi
done
