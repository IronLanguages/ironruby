#!/bin/bash
# Groups the failures and errors in one run.sh log by their first line, so that
# a hundred reports collapse into the handful of causes behind them.
#
#   Util/gems/triage.sh <log-dir>/<gem>.log [...]
set -u
for f in "$@"; do
  echo "===== $f"
  grep -aA2 -E '^ *[0-9]+\) (Failure|Error|Omission|Pending):' "$f" \
    | grep -aE '^[A-Za-z0-9_:]+#|^[A-Za-z:]+(Error|Exception)' \
    | sed -E 's/0x[0-9a-f]+/0xXX/g; s/:[0-9]+:/:N:/g' \
    | sort | uniq -c | sort -rn | head -25
done
