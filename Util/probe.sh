#!/bin/bash
# Classify a list of spec files read from stdin, printing the last few lines of
# output for each so the failure mode is visible.
IR_ROOT="$(dirname "$(dirname "$(readlink -f "$0")")")"
cd "$IR_ROOT" || exit 1
export RUBY_EXE="$IR_ROOT/ir.sh"
while read -r f; do
  [ -n "$f" ] || continue
  log="$(mktemp)"
  timeout -s KILL "${SWEEP_TIMEOUT:-60}" "$IR_ROOT/ir.sh" -Imspec/lib mspec/bin/mspec-run "$f" >"$log" 2>&1
  st=$?
  echo "=== $f exit=$st"
  if grep -qE '^[0-9]+ files?, [0-9]+ examples?' "$log"; then
    echo "TALLY OK: $(grep -m1 -E '^[0-9]+ files?, [0-9]+ examples?' "$log")"
  else
    tail -c 1200 "$log" | cat -v
  fi
  rm -f "$log"
done
