#!/bin/bash
# Times IronRuby (./ir.sh) against CRuby (`ruby`) on Util/bench/bench.rb and prints ratios.
#
#   Util/bench/run.sh [repetitions] [outfile] [benchmark...]
#
# Each benchmark runs in a fresh process and times only its own work, so interpreter
# start-up is out of the numbers.  The best of N repetitions is reported for each side,
# which is the right statistic for "how fast can this go" - the noise is all one-sided.
#
#   Util/bench/run.sh                       # every benchmark, best of 3
#   Util/bench/run.sh 5 out.txt call_empty  # just one, best of 5, tee'd to out.txt
#   IR=/path/to/ir.sh Util/bench/run.sh     # measure a different build
#   ./ir.sh Util/bench/bench.rb --list      # the benchmark names
#
# A benchmark takes an optional scale factor as its second argument
# (`ruby Util/bench/bench.rb call_empty 10`), which multiplies the iteration counts.
HERE="$(dirname "$(readlink -f "$0")")"
ROOT="$(dirname "$(dirname "$HERE")")"
IR="${IR:-$ROOT/ir.sh}"
RUBY="${RUBY:-ruby}"
N="${1:-3}"
OUT="${2:-/dev/null}"
shift 2 2>/dev/null
BENCHES="$*"
[ -z "$BENCHES" ] && BENCHES=$("$RUBY" "$HERE/bench.rb" --list)
TIMEOUT="${TIMEOUT:-600}"

best() {
  local b="" v
  for _i in $(seq 1 "$N"); do
    v=$(timeout "$TIMEOUT" "$@" 2>/dev/null | tail -1)
    case "$v" in
      *[0-9]*) : ;;
      *) v="" ;;
    esac
    [ -z "$v" ] && continue
    if [ -z "$b" ] || awk "BEGIN{exit !($v < $b)}"; then b="$v"; fi
  done
  [ -z "$b" ] && b="nan"
  echo "$b"
}

{
printf "%-22s %10s %10s %8s\n" "benchmark" "cruby" "ironruby" "ratio"
for bn in $BENCHES; do
  c=$(best "$RUBY" "$HERE/bench.rb" "$bn")
  r=$(best "$IR" "$HERE/bench.rb" "$bn")
  ratio=$(awk "BEGIN{if(\"$c\"==\"nan\"||\"$r\"==\"nan\"||$c==0){print \"-\"}else{printf \"%.2f\", $r/$c}}")
  printf "%-22s %10s %10s %8s\n" "$bn" "$c" "$r" "$ratio"
done
} | tee -a "$OUT"
