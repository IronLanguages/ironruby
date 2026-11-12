#!/bin/bash
# Per-file run of every spec/core file, one mspec process each.
#
#   Util/sweep-core.sh [outfile] [jobs]
#
# Emits one TSV row per spec file:
#
#   <exit status>\t<spec file>\t<mspec tally line | NO-TALLY>\t<cause>
#
# "cause" is only meaningful for NO-TALLY rows and is one of:
#   ABORT   process died on a .NET Debug.Assert (exit 134) or other signal
#   HANG    exceeded the per-file timeout (exit 124)
#   REPORT  the examples ran -- mspec printed progress -- but the run died
#           before the summary, so the tally never appeared
#   LOAD    the file never got as far as running an example
IR_ROOT="$(dirname "$(dirname "$(readlink -f "$0")")")"
OUT="${1:-$IR_ROOT/Util/sweep-core.tsv}"
JOBS="${2:-6}"
TIMEOUT="${SWEEP_TIMEOUT:-60}"

cd "$IR_ROOT" || exit 1
# IR lets a sweep run against a build other than the one in the tree, so a
# before/after pair can be measured at the same time under the same load.
IR="${IR:-$IR_ROOT/ir.sh}"
export RUBY_EXE="$IR"

run_one() {
  file="$1"
  log="$(mktemp)"
  timeout -s KILL "$TIMEOUT" "$IR" -Imspec/lib mspec/bin/mspec-run "$file" >"$log" 2>&1
  status=$?
  # -a matters: a spec that prints a NUL byte makes grep call the log a binary
  # file and report "Binary file ... matches" instead of the tally line, which
  # looks exactly like a spec that printed no tally at all.
  tally="$(grep -a -m1 -E '^[0-9]+ files?, [0-9]+ examples?' "$log")"
  if [ -n "$tally" ]; then
    printf '%s\t%s\t%s\t\n' "$status" "$file" "$tally"
  else
    case "$status" in
      124|137) cause=HANG ;;
      0|1) cause=REPORT ;;
      *) cause=ABORT ;;
    esac
    # Did mspec get as far as running examples? The dotted formatter prints
    # progress characters on their own line before anything else.
    if [ "$cause" = REPORT ] && ! grep -a -qE '^[.EF][.EF]*([^.EF]|$)|^[0-9]+\)$' "$log"; then
      cause=LOAD
    fi
    printf '%s\t%s\tNO-TALLY\t%s\n' "$status" "$file" "$cause"
  fi
  rm -f "$log"
}
export -f run_one
export IR_ROOT TIMEOUT IR

# SWEEP_RESUME=1 keeps the rows already in $OUT and only runs the rest, so a
# sweep interrupted by a machine-wide load spike can be picked up again.
if [ "${SWEEP_RESUME:-0}" = 1 ] && [ -s "$OUT" ]; then
  cut -f2 "$OUT" | sort -u > /tmp/sweep-done.$$
  find spec/core -name '*_spec.rb' | sort > /tmp/sweep-all.$$
  comm -23 /tmp/sweep-all.$$ /tmp/sweep-done.$$ |
    xargs -P "$JOBS" -I{} bash -c 'run_one "$@"' _ {} >> "$OUT"
  rm -f /tmp/sweep-done.$$ /tmp/sweep-all.$$
else
  find spec/core -name '*_spec.rb' | sort |
    xargs -P "$JOBS" -I{} bash -c 'run_one "$@"' _ {} > "$OUT"
fi

echo "wrote $OUT: $(wc -l < "$OUT") files, $(grep -c 'NO-TALLY' "$OUT") NO-TALLY"
