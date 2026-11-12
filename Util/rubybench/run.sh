#!/bin/bash
# Runs the ruby-bench (https://github.com/ruby/ruby-bench) benchmarks under
# IronRuby and CRuby, one process per benchmark per interpreter, and prints a
# table of milliseconds and the IronRuby/CRuby ratio.
#
#   Util/rubybench/run.sh                     # every micro benchmark
#   Util/rubybench/run.sh fib nbody           # just these
#   Util/rubybench/run.sh --group all         # micro + macro
#   Util/rubybench/run.sh --verify fib        # compare the answer, not the time
#   Util/rubybench/run.sh --nojit             # third column: IronRuby -X:NoJIT
#   IR_CONFIG=Release Util/rubybench/run.sh   # measure the optimized build
#
# Which benchmarks exist, and what is known about them, is in catalog.rb. This
# script never writes inside the ruby-bench checkout except where the benchmark
# itself does (a bundled benchmark's Gemfile.lock), and it never writes a
# results CSV into the repository - the logs go to a temp directory.
#
# Options:
#   --group micro|macro|all   which group to run when no names are given
#                             (default micro)
#   --verify                  run each benchmark once under both interpreters
#                             with a harness that prints the value the
#                             benchmark's block returns, and compare. A fast
#                             wrong answer is worse than a failure, so this is
#                             the check that matters; the time columns are
#                             meaningless in this mode.
#   --nojit                   also time IronRuby with -X:NoJIT and show it.
#                             A benchmark where NoJIT wins is a JIT regression.
#   --ir-only / --cruby-only  skip one side
#
# Environment:
#   RB_DIR      ruby-bench checkout   (default /root/workspace/ruby-bench)
#   IR_CONFIG   Debug (default) or Release - measure in Release
#   IR          the IronRuby launcher (default ./ir.sh next to this repo)
#   RUBY        the CRuby oracle      (default `ruby`)
#   RB_TIMEOUT  seconds per run       (default 600)
#   RB_WARMUP   warmup iterations     (default 5)
#   RB_ITRS     timed iterations      (default 5)
#   RB_MIN_TIME minimum seconds of timed work per run (default 0)
#   RB_LOG_DIR  where the per-run logs go
#   GEM_HOME    gem home for the benchmarks that bundle (default <repo>/.gems)
#
# Note on RSS: ruby-bench's harness-common.rb#get_maxrss needs Fiddle::Function,
# which IronRuby does not have yet, so return_results raises at the very end of
# every run - after the per-iteration times have already been printed. That is
# why the timings are read from the "#N: NNNms" lines on stdout rather than from
# the harness's JSON: the numbers are real, only the memory report is missing.
# The ruby-bench sources are never patched; when Fiddle lands the runs simply
# stop raising and nothing here has to change.

set -u

HERE="$(dirname "$(readlink -f "$0")")"
ROOT="$(dirname "$(dirname "$HERE")")"
: "${IR:=$ROOT/ir.sh}"
: "${RUBY:=ruby}"
: "${RB_DIR:=/root/workspace/ruby-bench}"
: "${RB_TIMEOUT:=600}"
: "${RB_WARMUP:=5}"
: "${RB_ITRS:=5}"
: "${RB_MIN_TIME:=0}"
: "${GEM_HOME:=$ROOT/.gems}"
export GEM_HOME

GROUP=micro
VERIFY=0
NOJIT=0
DO_IR=1
DO_CRUBY=1
NAMES=()

while [ $# -gt 0 ]; do
  case "$1" in
    --group)   GROUP="$2"; shift 2 ;;
    --group=*) GROUP="${1#*=}"; shift ;;
    --verify)  VERIFY=1; shift ;;
    --nojit)   NOJIT=1; shift ;;
    --ir-only) DO_CRUBY=0; shift ;;
    --cruby-only) DO_IR=0; shift ;;
    -h|--help) sed -n '2,50p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    -*)        echo "unknown option: $1" >&2; exit 2 ;;
    *)         NAMES+=("$1"); shift ;;
  esac
done

case "$GROUP" in micro|macro|all) ;; *) echo "--group must be micro, macro or all" >&2; exit 2 ;; esac
[ -d "$RB_DIR/benchmarks" ] || { echo "no ruby-bench checkout at $RB_DIR (set RB_DIR)" >&2; exit 1; }

LOG_DIR="${RB_LOG_DIR:-${TMPDIR:-/tmp}/ir-rubybench-$$}"
mkdir -p "$LOG_DIR"

# The catalog is plain data; read it with whichever interpreter is around.
CAT="$RUBY"
command -v "$CAT" >/dev/null 2>&1 || CAT="$IR"
CATALOG=$("$CAT" "$HERE/catalog.rb" dump "$RB_DIR" "$GROUP" "${NAMES[@]+${NAMES[@]}}") || exit 1
[ -n "$CATALOG" ] || { echo "no benchmarks in group $GROUP" >&2; exit 1; }

# --verify replaces ruby-bench's harness with a shim of our own, put ahead of it
# on -I so the benchmark's `require "harness"` finds this one. It reuses the
# real harness-common (seeded RNG, use_gemfile, ...) but runs the block exactly
# once and prints what it returned, so the two interpreters can be compared on
# the answer. harness-common's return_results - the part that needs Fiddle - is
# never reached. Nothing under $RB_DIR is modified.
VERIFY_DIR="$LOG_DIR/verify-harness"
mkdir -p "$VERIFY_DIR"
cat >"$VERIFY_DIR/harness.rb" <<'SHIM'
require File.expand_path('harness-common', ENV.fetch('RB_HARNESS_DIR'))

WARMUP_ITRS = 0
MIN_BENCH_ITRS = 1
MIN_BENCH_TIME = 0

puts RUBY_DESCRIPTION

def run_benchmark(_num_itrs_hint = nil, **, &block)
  value = block.call
  text = begin
    value.inspect
  rescue Exception => e
    "<inspect raised #{e.class}>"
  end
  # Object addresses and the order of an unordered dump are not answers.
  text = text.gsub(/0x[0-9a-fA-F]+/, '0xADDR')
  puts "##RESULT## #{value.class} len=#{text.bytesize} #{text.byteslice(0, 2000)}"
end
SHIM

# Pull the "#N: NNNms" lines the harness prints, drop the warmups, keep the
# fastest of what is left. Minimum, not mean: the noise on a timed loop is all
# one-sided, so the best iteration is the honest "how fast can this go".
best_ms() {
  grep -aoE '^ *#[0-9]+: +[0-9]+ms' "$1" \
    | grep -oE '[0-9]+ms' | tr -d 'ms' \
    | awk -v w="$RB_WARMUP" 'NR>w {if (b=="" || $1<b) b=$1} END {print (b=="" ? "" : b)}'
}

# The known missing-RSS path: get_maxrss wants Fiddle::Function, which IronRuby
# does not have, so return_results raises after the last iteration has already
# been timed and printed. It is not a benchmark failure and must not be reported
# as one - but it is not silence either, so the runner says so once at the end.
FIDDLE_SEEN=0
rss_only_failure() { grep -aq "uninitialized constant Fiddle::Function" "$1"; }

# $1 log, $2 exit code -> a one-line reason, or empty when it looks like a pass.
failure_note() {
  if [ "$2" = 124 ]; then echo "TIMEOUT after ${RB_TIMEOUT}s"; return; fi
  local err
  err=$(grep -a -E '^[^ ].*\((NameError|NoMethodError|NotImplementedError|ArgumentError|TypeError|RuntimeError|LoadError|StandardError|SystemStackError|[A-Za-z:]+Error)\)$' "$1" \
        | grep -av 'uninitialized constant Fiddle::Function' | tail -1)
  [ -n "$err" ] && { echo "${err##*/}"; return; }
  [ "$2" = 0 ] && return
  if rss_only_failure "$1"; then FIDDLE_SEEN=1; return; fi
  echo "exit $2"
}

run_one() { # $1 path, $2 log, $3... extra interpreter args
  local path="$1" log="$2"; shift 2
  ( cd "$(dirname "$path")" \
    && WARMUP_ITRS="$RB_WARMUP" MIN_BENCH_ITRS="$RB_ITRS" MIN_BENCH_TIME="$RB_MIN_TIME" \
       RESULT_JSON_PATH="$LOG_DIR/result.json" OUT_CSV_PATH="$LOG_DIR/result.csv" \
       RB_HARNESS_DIR="$RB_DIR/harness" \
       timeout "$RB_TIMEOUT" "$@" "$INC" "$(basename "$path")" ) >"$log" 2>&1
  return $?
}

if [ "$VERIFY" = 1 ]; then
  INC="-I$VERIFY_DIR"
  RB_WARMUP=0; RB_ITRS=1; RB_MIN_TIME=0
  fmt='%-24s %-6s %-9s %s\n'
  printf "$fmt" BENCHMARK GROUP VERDICT NOTE
  printf '%.0s-' {1..96}; echo
else
  INC="-I$RB_DIR/harness"
  if [ "$NOJIT" = 1 ]; then
    fmt='%-24s %-6s %9s %9s %9s %7s %-8s %s\n'
    printf "$fmt" BENCHMARK GROUP "IR(ms)" "NOJIT(ms)" "CRUBY(ms)" RATIO STATUS NOTE
    printf '%.0s-' {1..110}; echo
  else
    fmt='%-24s %-6s %9s %9s %7s %-8s %s\n'
    printf "$fmt" BENCHMARK GROUP "IR(ms)" "CRUBY(ms)" RATIO STATUS NOTE
    printf '%.0s-' {1..100}; echo
  fi
fi

while IFS=$'\x1f' read -r name group path gems na note unstable; do
  [ -n "$name" ] || continue

  if [ -n "$na" ]; then
    if [ "$VERIFY" = 1 ]; then printf "$fmt" "$name" "$group" "N/A" "$na"
    elif [ "$NOJIT" = 1 ]; then printf "$fmt" "$name" "$group" - - - - "N/A" "$na"
    else printf "$fmt" "$name" "$group" - - - "N/A" "$na"; fi
    continue
  fi
  if [ ! -f "$path" ]; then
    reason="not in this ruby-bench checkout: ${path#$RB_DIR/}"
    if [ "$VERIFY" = 1 ]; then printf "$fmt" "$name" "$group" "N/A" "$reason"
    elif [ "$NOJIT" = 1 ]; then printf "$fmt" "$name" "$group" - - - - "N/A" "$reason"
    else printf "$fmt" "$name" "$group" - - - "N/A" "$reason"; fi
    continue
  fi
  [ "$gems" = 1 ] && mkdir -p "$GEM_HOME"

  if [ "$VERIFY" = 1 ]; then
    if [ -n "$unstable" ]; then
      printf "$fmt" "$name" "$group" "SKIP" "no stable value to compare: $unstable"
      continue
    fi
    run_one "$path" "$LOG_DIR/$name.ir.verify.log" "$IR"; irc=$?
    run_one "$path" "$LOG_DIR/$name.cruby.verify.log" "$RUBY"; crc=$?
    irv=$(grep -a '^##RESULT## ' "$LOG_DIR/$name.ir.verify.log" | tail -1)
    crv=$(grep -a '^##RESULT## ' "$LOG_DIR/$name.cruby.verify.log" | tail -1)
    if [ -z "$crv" ]; then
      printf "$fmt" "$name" "$group" "?" "CRuby produced no value ($(failure_note "$LOG_DIR/$name.cruby.verify.log" $crc))"
    elif [ -z "$irv" ]; then
      printf "$fmt" "$name" "$group" "FAIL" "IronRuby produced no value ($(failure_note "$LOG_DIR/$name.ir.verify.log" $irc))"
    elif [ "$irv" = "$crv" ]; then
      printf "$fmt" "$name" "$group" "SAME" "${irv:11:60}"
    else
      printf "$fmt" "$name" "$group" "DIFFER" "ir=${irv:11:40} cruby=${crv:11:40}"
    fi
    continue
  fi

  ir=- irn=- cr=- st=OK n="$note"
  if [ "$DO_IR" = 1 ]; then
    run_one "$path" "$LOG_DIR/$name.ir.log" "$IR"; rc=$?
    ir=$(best_ms "$LOG_DIR/$name.ir.log")
    rss_only_failure "$LOG_DIR/$name.ir.log" && FIDDLE_SEEN=1
    f=$(failure_note "$LOG_DIR/$name.ir.log" $rc)
    if [ -z "$ir" ]; then
      ir=-
      case "$f" in *TIMEOUT*) st=TIMEOUT ;; *) st=FAIL ;; esac
      n="${f:-no iteration times on stdout}${note:+; $note}"
    elif [ -n "$f" ]; then
      st=FAIL; n="$f${note:+; $note}"
    fi
  fi
  if [ "$NOJIT" = 1 ] && [ "$DO_IR" = 1 ]; then
    run_one "$path" "$LOG_DIR/$name.nojit.log" "$IR" -X:NoJIT
    irn=$(best_ms "$LOG_DIR/$name.nojit.log"); [ -z "$irn" ] && irn=-
  fi
  if [ "$DO_CRUBY" = 1 ]; then
    run_one "$path" "$LOG_DIR/$name.cruby.log" "$RUBY"; rc=$?
    cr=$(best_ms "$LOG_DIR/$name.cruby.log"); [ -z "$cr" ] && cr=-
    if [ "$cr" = - ] && [ "$st" = OK ]; then
      n="CRuby did not produce a time: $(failure_note "$LOG_DIR/$name.cruby.log" $rc)${note:+; $note}"
    fi
  fi
  ratio=$(awk -v a="$ir" -v b="$cr" 'BEGIN{if(a=="-"||b=="-"||b+0==0){print "-"}else{printf "%.2f", a/b}}')
  # A JIT that loses to no JIT at all is worth saying out loud.
  if [ "$irn" != - ] && [ "$ir" != - ]; then
    awk -v a="$ir" -v b="$irn" 'BEGIN{exit !(b < a*0.95)}' && n="NoJIT is faster${n:+; $n}"
  fi
  if [ "$NOJIT" = 1 ]; then printf "$fmt" "$name" "$group" "$ir" "$irn" "$cr" "$ratio" "$st" "$n"
  else printf "$fmt" "$name" "$group" "$ir" "$cr" "$ratio" "$st" "$n"; fi
done <<<"$CATALOG"

echo
if [ "$FIDDLE_SEEN" = 1 ]; then
  echo "note: IronRuby runs ended with a Fiddle::Function NameError from the harness's"
  echo "      get_maxrss, after the iterations were timed. The times are good; the RSS"
  echo "      figures are simply unavailable until Fiddle::Function exists."
fi
echo "logs: $LOG_DIR"
