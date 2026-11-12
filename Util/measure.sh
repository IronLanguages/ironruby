#!/bin/bash
# Runs each argument through mspec separately and prints the one-line tally.
# Usage: Util/measure.sh spec/core/exception spec/core/kernel/raise_spec.rb ...
cd "$(dirname "$0")/.."
for f in "$@"; do
  line=$(RUBY_EXE=./ir.sh timeout 900 ./ir.sh -Imspec/lib mspec/bin/mspec-run "$f" 2>&1 | grep -E 'examples?,' | tail -1)
  printf '%-50s %s\n' "$f" "$line"
done
