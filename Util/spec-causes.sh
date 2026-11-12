#!/bin/bash
# Groups a spec directory's failures and errors by cause, by running each file
# and collecting the exception lines and the "Expected ..." lines the dotted
# formatter prints for a failure.
#
#   Util/spec-causes.sh <spec dir> [jobs]
IR_ROOT="$(dirname "$(dirname "$(readlink -f "$0")")")"
cd "$IR_ROOT" || exit 1
DIR="$1"
JOBS="${2:-8}"
IR="${IR:-$IR_ROOT/ir.sh}"
export RUBY_EXE="$IR"
OUT="${OUT:-/tmp/rk/causes.log}"
: > "$OUT"

run_one() {
  timeout -s KILL "${SWEEP_TIMEOUT:-120}" "$IR" -Imspec/lib mspec/bin/mspec-run "$1" 2>&1 </dev/null
}
export -f run_one
export IR

find "$DIR" -name '*_spec.rb' | sort |
  xargs -P "$JOBS" -I{} bash -c 'run_one "$@"' _ {} >> "$OUT"

echo "=== errors by class and message"
grep -a -E '^[A-Za-z:]+(Error|Exception|Errno::[A-Z]+): ' "$OUT" |
  sed 's/[0-9]\+/N/g' | cut -c1-110 | sort | uniq -c | sort -rn | head -25
echo
echo "=== failures by expectation"
grep -a -E '^Expected ' "$OUT" | sed 's/[0-9]\+/N/g' | cut -c1-100 |
  sort | uniq -c | sort -rn | head -20
