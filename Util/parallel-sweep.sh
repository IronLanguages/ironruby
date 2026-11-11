#!/bin/bash
# Runs spec/core, spec/library and spec/language in parallel and prints a short summary.
#
# One mspec-run per spec directory (the top-level files of a suite in chunks of 8), JOBS of
# them at a time, biggest first, so the work balances itself however slow a directory is.
# Each job is a fresh process, so an example can no longer be disturbed by whatever an
# unrelated directory left behind - which means results differ slightly from one long
# sequential run; compare sweeps made by this script only with each other.
#
# Usage:  Util/parallel-sweep.sh OUT_DIR [BASELINE_OUT_DIR]
#   IR=./ir.sh           interpreter script the specs run under (also RUBY_EXE for ruby_exe)
#   JOBS=8               parallel jobs
#   SUITES="core library language command_line security"
#   SHARD=K/N            run only every Nth job, starting at the Kth of the size-sorted list, so
#                        N machines can split a suite (.github/workflows/spec-sweep.yml does)
#   JOB_TIMEOUT=3600     seconds one mspec-run may take before it is killed
#   IR_CONFIG / IR_TFM are read by ir.sh itself, so `IR_TFM=net11.0 Util/parallel-sweep.sh out`
#   sweeps the .NET 11 build.
#
# OUT_DIR/<suite>.txt gets the sorted unique names of failing examples, OUT_DIR/<suite>/*.log
# the logs, OUT_DIR/jobs.txt the jobs that ran ("<suite> <job> <files...>", one per line). With a baseline, the summary lists the names that are new and the ones fixed.
# A job whose log has no mspec summary line (it crashed or hung) is reported by name: its
# examples are missing from the counts, so a sweep with one is not a clean comparison.
set -u
cd "$(dirname "$0")/.."

OUT=${1:?usage: Util/parallel-sweep.sh OUT_DIR [BASELINE_OUT_DIR]}
BASE=${2:-}
IR=${IR:-./ir.sh}
JOBS=${JOBS:-8}
SUITES=${SUITES:-"core library language command_line security"}
SHARD=${SHARD:-1/1}
JOB_TIMEOUT=${JOB_TIMEOUT:-3600}
case "$SHARD" in
  [1-9]*/[1-9]*) ;;
  *) echo "SHARD must be K/N, not $SHARD" >&2; exit 2 ;;
esac

mkdir -p "$OUT"
jobs_file="$OUT/jobs.txt"
: > "$jobs_file"
for s in $SUITES; do
  rm -rf "${OUT:?}/$s"
  mkdir -p "$OUT/$s"
  for d in spec/$s/*/; do
    [ -d "$d" ] || continue
    # no trailing blank: xargs -L takes one as "this line continues on the next"
    files=$(find "$d" -name '*_spec.rb' | sort | paste -sd' ')
    [ -n "$files" ] && echo "$s $(basename "$d") $files" >> "$jobs_file"
  done
  i=0
  find "spec/$s" -maxdepth 1 -name '*_spec.rb' | sort | xargs -r -n 8 echo | while read -r chunk; do
    i=$((i + 1))
    echo "$s top$i $chunk" >> "$jobs_file"
  done
done

run_job() {
  local suite=$1 name=$2
  shift 2
  # -k: a run that ignores the TERM is killed, so a wedged process cannot outlive the sweep
  RUBY_EXE=$IR timeout -k 30 "$JOB_TIMEOUT" $IR -Imspec/lib mspec/bin/mspec-run "$@" > "$OUT/$suite/$name.log" 2>&1
}
export -f run_job
export OUT IR JOB_TIMEOUT

start=$(date +%s)
# sort -k1,1nr -s keeps the order of equal sizes, so every shard computes the same split
awk '{ print NF, $0 }' "$jobs_file" | sort -s -k1,1nr | cut -d' ' -f2- |
  awk -v k="${SHARD%/*}" -v n="${SHARD#*/}" '(NR - 1) % n == k - 1' > "$jobs_file.run"
mv "$jobs_file.run" "$jobs_file"
xargs -P "$JOBS" -L 1 -s 1000000 bash -c 'run_job "$@"' _ < "$jobs_file"
end=$(date +%s)

for s in $SUITES; do
  examples=0; failures=0; errors=0; missing=""
  for log in "$OUT/$s"/*.log; do
    line=$(grep -aP '\d+ examples?, ' "$log" | tail -1)
    if [ -z "$line" ]; then
      missing="$missing $(basename "$log" .log)"
      continue
    fi
    examples=$((examples + $(echo "$line" | grep -oP '\d+(?= examples?)')))
    failures=$((failures + $(echo "$line" | grep -oP '\d+(?= failures?)')))
    errors=$((errors + $(echo "$line" | grep -oP '\d+(?= errors?)')))
  done
  grep -a -h -oP '^(.*) (FAILED|ERROR)$' "$OUT/$s"/*.log | sed -E 's/ (FAILED|ERROR)$//' | sort -u > "$OUT/$s.txt"
  echo "$s: $(wc -l < "$OUT/$s.txt") failing ($examples examples, $failures failures, $errors errors)${missing:+ - NO SUMMARY:$missing}"
  if [ -n "$BASE" ] && [ -f "$BASE/$s.txt" ]; then
    comm -13 "$BASE/$s.txt" "$OUT/$s.txt" | sed 's/^/  new:   /'
    comm -23 "$BASE/$s.txt" "$OUT/$s.txt" | sed 's/^/  fixed: /'
  fi
done
echo "wall: $((end - start))s with $JOBS jobs"
