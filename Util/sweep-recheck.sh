#!/bin/bash
# Re-run only the NO-TALLY rows of a sweep, serially and with a longer timeout,
# and rewrite those rows in place.
#
#   Util/sweep-recheck.sh Util/sweep-core.tsv [timeout]
#
# The machine runs several agents at once; under load a spec that normally
# finishes in three seconds can blow a 60s budget, and a stray `pkill` from a
# neighbouring worktree shows up as SIGTERM. Both look like NO-TALLY in the
# first pass, so the small NO-TALLY set is always re-measured before it is
# believed.
IR_ROOT="$(dirname "$(dirname "$(readlink -f "$0")")")"
SWEEP="$1"
TIMEOUT="${2:-180}"
cd "$IR_ROOT" || exit 1
IR="${IR:-$IR_ROOT/ir.sh}"
export RUBY_EXE="$IR"

tmp="$(mktemp)"
while IFS=$'\t' read -r status file tally cause; do
  case "$tally" in
    NO-TALLY|"Binary file"*|"") ;;                 # re-measure
    *) printf '%s\t%s\t%s\t%s\n' "$status" "$file" "$tally" "$cause"; continue ;;
  esac
  log="$(mktemp)"
  timeout -s KILL "$TIMEOUT" "$IR" -Imspec/lib mspec/bin/mspec-run "$file" >"$log" 2>&1
  st=$?
  new="$(grep -a -m1 -E '^[0-9]+ files?, [0-9]+ examples?' "$log")"
  if [ -n "$new" ]; then
    printf '%s\t%s\t%s\t\n' "$st" "$file" "$new"
  else
    case "$st" in
      124|137) c=HANG ;;
      0|1) c=REPORT ;;
      *) c=ABORT ;;
    esac
    if [ "$c" = REPORT ] && ! grep -a -qE '^[.EF][.EF]*([^.EF]|$)|^[0-9]+\)$' "$log"; then
      c=LOAD
    fi
    printf '%s\t%s\tNO-TALLY\t%s\n' "$st" "$file" "$c"
  fi
  rm -f "$log"
done < "$SWEEP" > "$tmp"
mv "$tmp" "$SWEEP"
echo "rechecked: $(grep -c 'NO-TALLY' "$SWEEP") still NO-TALLY"
