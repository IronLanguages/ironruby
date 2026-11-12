#!/bin/bash
# Runs the pure-Ruby gem macro benchmarks of ruby-bench under IronRuby and under
# CRuby, one after the other, and prints the pair of times per benchmark.  It is
# what produced the table in pure-gems.md; see that file for the results and for
# the benchmarks that cannot run here at all.
#
#   Util/rubybench/pure-gems-run.sh /path/to/ruby-bench [benchmark ...]
#
# With no benchmark names it runs the whole pure-gem set.  A name is a directory
# under <ruby-bench>/benchmarks; `addressable:parse.rb` picks one script out of a
# directory that holds several (the addressable family).
#
# Env:
#   WARMUP_ITRS / MIN_BENCH_ITRS / MIN_BENCH_TIME  as ruby-bench's harness takes them
#   IR_CONFIG=Release                              which IronRuby build to time (default)
#   CRUBY=ruby                                     the oracle
#   OUT_DIR                                        where the per-run logs go
#
# Do not run this inside a shared ruby-bench checkout: the benchmarks chdir into
# their own directory and `bundle install` there.  Copy it first.

set -u
IR_ROOT="$(cd "$(dirname "$(readlink -f "$0")")/../.." && pwd)"
RB=${1:?usage: pure-gems-run.sh <ruby-bench dir> [benchmark ...]}
shift

: "${IR_CONFIG:=Release}"
: "${CRUBY:=ruby}"
: "${WARMUP_ITRS:=1}"
: "${MIN_BENCH_ITRS:=3}"
: "${MIN_BENCH_TIME:=1}"
: "${OUT_DIR:=$RB/data/pure-gems}"
export IR_CONFIG WARMUP_ITRS MIN_BENCH_ITRS MIN_BENCH_TIME

ALL="erubi etanni chunky-png liquid-render liquid-compile liquid-il psych-load
     addressable:parse.rb addressable:equality.rb addressable:getters.rb
     addressable:join.rb addressable:merge.rb addressable:new.rb
     addressable:normalize.rb addressable:setters.rb addressable:to-s.rb
     graphql hexapdf rubykon protoboeuf protoboeuf-encode optcarrot rubyboy tinygql"
[ $# -gt 0 ] || set -- $ALL

mkdir -p "$OUT_DIR"

# `bundle install`, which the benchmarks run themselves through use_gemfile, has to
# find IronRuby's own bundler and gem - and its own gem home, so that parallel work
# in another worktree does not race with it.
SHIM=$(mktemp -d)
trap 'rm -rf "$SHIM"' EXIT
for name in ruby bundle bundler gem; do
  case $name in
    ruby) printf '#!/bin/sh\nexec %s "$@"\n' "$IR_ROOT/ir.sh" > "$SHIM/$name" ;;
    *)    printf '#!/bin/sh\nexec %s -S %s "$@"\n' "$IR_ROOT/ir.sh" "$name" > "$SHIM/$name" ;;
  esac
  chmod +x "$SHIM/$name"
done

run() { # run <engine> <script> <logfile>
  local engine=$1 script=$2 log=$3
  if [ "$engine" = ir ]; then
    ( cd "$RB" && GEM_HOME="$IR_ROOT/.gems" PATH="$SHIM:$PATH" \
        OUT_CSV_PATH="$log.csv" "$IR_ROOT/ir.sh" -I "$RB/harness" "$script" ) > "$log" 2>&1
  else
    ( cd "$RB" && OUT_CSV_PATH="$log.csv" "$CRUBY" -I "$RB/harness" "$script" ) > "$log" 2>&1
  fi
}

ms() { grep -a "Average of last" "$1" | tail -1 | grep -oE "[0-9]+ms"; }

for spec in "$@"; do
  name=${spec%%:*}
  file=${spec#*:}; [ "$file" = "$spec" ] && file=benchmark.rb
  script=$RB/benchmarks/$name/$file
  [ -f "$script" ] || script=$RB/benchmarks/$name.rb
  if [ ! -f "$script" ]; then
    echo "$spec: no such benchmark"
    continue
  fi

  base=$OUT_DIR/${spec//:/-}
  run ir    "$script" "$base.ir.log";    ir_status=$?
  run cruby "$script" "$base.cruby.log"; cr_status=$?
  ir_ms=$(ms "$base.ir.log"); cr_ms=$(ms "$base.cruby.log")
  ratio=""
  if [ -n "$ir_ms" ] && [ -n "$cr_ms" ]; then
    ratio=$(awk "BEGIN { printf \"%.1fx\", ${ir_ms%ms} / ${cr_ms%ms} }")
  fi
  printf '%-28s ironruby=%-10s cruby=%-9s %s\n' \
    "$spec" "${ir_ms:-FAIL($ir_status)}" "${cr_ms:-FAIL($cr_status)}" "$ratio"
done
