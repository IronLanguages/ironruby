#!/bin/bash
# Per-file sweep of one spec directory, in the same format as sweep-core.sh.
# Separate from it so a single directory can be measured without paying for the
# whole of spec/core.
#
#   Util/sweep-dir.sh <spec dir> <outfile> [jobs]
IR_ROOT="$(dirname "$(dirname "$(readlink -f "$0")")")"
DIR="$1"
OUT="$2"
JOBS="${3:-8}"
TIMEOUT="${SWEEP_TIMEOUT:-120}"

cd "$IR_ROOT" || exit 1
IR="${IR:-$IR_ROOT/ir.sh}"
export RUBY_EXE="$IR"

run_one() {
  file="$1"
  log="$(mktemp)"
  start=$(date +%s%N)
  timeout -s KILL "$TIMEOUT" "$IR" -Imspec/lib mspec/bin/mspec-run "$file" >"$log" 2>&1 </dev/null
  status=$?
  end=$(date +%s%N)
  ms=$(( (end - start) / 1000000 ))
  # -a: a spec that prints a NUL makes grep call the log binary and report
  # "Binary file ... matches" instead of the tally line.
  tally="$(grep -a -m1 -E '^[0-9]+ files?, [0-9]+ examples?' "$log")"
  if [ -n "$tally" ]; then
    printf '%s\t%s\t%s\t\t%s\n' "$status" "$file" "$tally" "$ms"
  else
    case "$status" in
      124|137) cause=HANG ;;
      0|1) cause=REPORT ;;
      *) cause=ABORT ;;
    esac
    if [ "$cause" = REPORT ] && ! grep -a -qE '^[.EF][.EF]*([^.EF]|$)|^[0-9]+\)$' "$log"; then
      cause=LOAD
    fi
    printf '%s\t%s\tNO-TALLY\t%s\t%s\n' "$status" "$file" "$cause" "$ms"
  fi
  rm -f "$log"
}
export -f run_one
export IR_ROOT TIMEOUT IR

find "$DIR" -name '*_spec.rb' | sort |
  xargs -P "$JOBS" -I{} bash -c 'run_one "$@"' _ {} > "$OUT"

echo "wrote $OUT: $(wc -l < "$OUT") files, $(grep -c 'NO-TALLY' "$OUT") NO-TALLY"
echo "slowest:"
sort -t"$(printf '\t')" -k5 -rn "$OUT" | head -5 | cut -f2,5
echo "total seconds: $(cut -f5 "$OUT" | paste -sd+ | bc | awk '{printf "%.0f", $1/1000}')"
