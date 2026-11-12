#!/bin/bash
# Per-file mspec sweep.  Each spec file is run in its own mspec process so that
# one aborting file cannot take the rest of the directory down with it.
#
#   Util/sweep.sh out.tsv spec/core/dir spec/core/integer ...
#
# Output is TSV: <file> <TAB> <tally line or NO-TALLY>.
# "NO-TALLY" means the run died before mspec could print its summary line.
cd "$(dirname "$0")/.."
OUT=$1; shift
: > "$OUT"
FILES=$(find "$@" -name '*_spec.rb' | sort)
run_one() {
  line=$(RUBY_EXE=./ir.sh timeout 300 ./ir.sh -Imspec/lib mspec/bin/mspec-run "$1" 2>&1 | grep -E 'examples?,' | tail -1)
  printf '%s\t%s\n' "$1" "${line:-NO-TALLY}"
}
export -f run_one
echo "$FILES" | xargs -P 8 -I{} bash -c 'run_one "$@"' _ {} >> "$OUT"
sort -o "$OUT" "$OUT"
echo "done: $(wc -l < "$OUT") files"
