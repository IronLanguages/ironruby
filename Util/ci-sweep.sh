#!/bin/bash
# The nightly sweep's runner (.github/workflows/spec-sweep.yml): Util/parallel-sweep.sh, then
# - a second pass over every job whose log has no mspec summary line, which runs that job's
#   files one at a time, each with its own timeout. A crash or a hang then costs one file
#   rather than a whole directory, the file is named, and the directory's other files count;
# - a third pass that runs each file with a failure Util/spec-known-failures.txt does not list
#   once more on its own (CLAUDE.md: timing-sensitive examples flip under a sweep's load, so a
#   new one is rerun alone before it is believed). It needs a Ruby to run Util/sweep-check.rb.
#
# Usage:  Util/ci-sweep.sh OUT_DIR
#   SUITES, SHARD, JOBS, JOB_TIMEOUT, IR    as for Util/parallel-sweep.sh
#   FILE_TIMEOUT=600                        seconds one file may take in the passes after it
#
# Besides what parallel-sweep.sh writes, OUT_DIR gets
#   shard.txt     "<suite> <K/N>" for each suite swept, so Util/sweep-check.rb can tell that
#                 every shard of a suite arrived
#   recheck.tsv   suite, job, file, exit status and log of each file of the second pass
#   rerun.tsv     suite, file, exit status and log of each file of the third pass
# (the logs' paths relative to OUT_DIR)
# Util/sweep-check.rb reads OUT_DIR (or several, one per shard) and compares it with
# Util/spec-known-failures.txt; this script only runs the specs and exits 0 once they ran, or 1
# when the file-by-file pass could not run all of its files.
set -u
cd "$(dirname "$0")/.."

OUT=${1:?usage: Util/ci-sweep.sh OUT_DIR}
IR=${IR:-./ir.sh}
JOBS=${JOBS:-8}
SUITES=${SUITES:-"core library language command_line security"}
SHARD=${SHARD:-1/1}
FILE_TIMEOUT=${FILE_TIMEOUT:-600}
CHECK_RUBY=${CHECK_RUBY:-ruby}
export SUITES SHARD

Util/parallel-sweep.sh "$OUT" || exit
for s in $SUITES; do echo "$s $SHARD"; done > "$OUT/shard.txt"

# The same summary-line test as parallel-sweep.sh's.
has_summary() { grep -aqP '\d+ examples?, ' "$1"; }

run_file() {
  local suite=$1 job=$2 file=$3
  # the log's path is kept relative to OUT_DIR, which is somewhere else once downloaded
  local log="$suite/$job.recheck/$(echo "$file" | tr / _).log"
  RUBY_EXE=$IR timeout -k 30 "$FILE_TIMEOUT" $IR -Imspec/lib mspec/bin/mspec-run "$file" \
    > "$OUT/$log" 2>&1 < /dev/null
  # one short line per write, so the parallel appends do not interleave
  printf '%s\t%s\t%s\t%s\t%s\n' "$suite" "$job" "$file" "$?" "$log" >> "$OUT/recheck.tsv"
}
rerun_file() {
  local suite=$1 file=$2
  local log="$suite/rerun/$(echo "$file" | tr / _).log"
  RUBY_EXE=$IR timeout -k 30 "$FILE_TIMEOUT" $IR -Imspec/lib mspec/bin/mspec-run "$file" \
    > "$OUT/$log" 2>&1 < /dev/null
  printf '%s\t%s\t%s\t%s\n' "$suite" "$file" "$?" "$log" >> "$OUT/rerun.tsv"
}
export -f run_file rerun_file
export OUT IR FILE_TIMEOUT

: > "$OUT/recheck.tsv"
: > "$OUT/recheck-files.txt"
while read -r suite job files; do
  has_summary "$OUT/$suite/$job.log" && continue
  echo "no summary line from $suite/$job: running its files one at a time"
  rm -rf "${OUT:?}/$suite/$job.recheck"
  mkdir -p "$OUT/$suite/$job.recheck"
  for f in $files; do echo "$suite $job $f"; done >> "$OUT/recheck-files.txt"
done < "$OUT/jobs.txt"
# run_file's status is its printf's, so a failing or crashing spec file does not make xargs
# fail: a nonzero status here means a worker could not be started or did not finish, and some
# files may have no row in recheck.tsv. The list of files is kept, to see which.
if ! xargs -r -P "$JOBS" -L 1 bash -c 'run_file "$@"' _ < "$OUT/recheck-files.txt"; then
  echo "running the files one at a time failed; the files are in $OUT/recheck-files.txt" >&2
  exit 1
fi
rm -f "$OUT/recheck-files.txt"

if [ -s "$OUT/recheck.tsv" ]; then
  sort -o "$OUT/recheck.tsv" "$OUT/recheck.tsv"
  while IFS=$'\t' read -r suite job file status log; do
    has_summary "$OUT/$log" || echo "  still no summary line (exit status $status): $file"
  done < "$OUT/recheck.tsv"
fi

: > "$OUT/rerun.tsv"
$CHECK_RUBY Util/sweep-check.rb --new-files "$OUT" > "$OUT/rerun-files.txt" ||
  echo "Util/sweep-check.rb --new-files failed; no file is run again"
if [ -s "$OUT/rerun-files.txt" ]; then
  echo "running $(wc -l < "$OUT/rerun-files.txt") file(s) with a new failure again, alone"
  for s in $SUITES; do rm -rf "${OUT:?}/$s/rerun"; mkdir -p "$OUT/$s/rerun"; done
  xargs -r -P "$JOBS" -L 1 bash -c 'rerun_file "$@"' _ < "$OUT/rerun-files.txt"
  sort -o "$OUT/rerun.tsv" "$OUT/rerun.tsv"
fi
rm -f "$OUT/rerun-files.txt"
exit 0
