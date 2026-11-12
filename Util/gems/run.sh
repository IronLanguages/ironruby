#!/bin/bash
# Requires each of CRuby's default and bundled gems under IronRuby and runs a
# test suite for it, one process per gem, then prints a table.
#
#   Util/gems/run.sh                # the stdlib tier: default and bundled gems
#   Util/gems/run.sh uri json       # just these
#   Util/gems/run.sh popular        # the popular tier: rubygems.org's top list
#   Util/gems/run.sh all            # both tiers
#
# The popular tier is not on disk. Install it first, into a GEM_HOME of your
# own (the user gem dir is shared, and concurrent agents corrupt each other's):
#
#   export GEM_HOME="$PWD/.gems"
#   Util/gems/install-popular.sh
#   Util/gems/run.sh popular
#
# A popular gem that ships no test/ is measured by Util/gems/exercises/<gem>.rb,
# a small but real use of the gem, run under IronRuby and under CRuby 4.0.6 and
# diffed - CRuby is the oracle and byte-identical stdout is the pass. The FROM
# column says which it was: unit, spec or exer.
#
# Where the suite comes from, in order: the gem's own test/ if it ships one, the
# matching directory in an unpacked CRuby source tree, or ruby/spec's
# spec/library/<name> run with the gem's lib/ on the path. Nothing is
# downloaded; a gem with none of the three is reported "load only", which is not
# a pass and the table says so.
#
# Environment:
#   GEM_DIR     installed gems          (default /usr/local/lib/ruby/gems/4.0.0/gems)
#   RUBY_SRC    unpacked CRuby source   (default /root/workspace/cosmoruby-upgrade/ruby-4.0.6)
#   IR_CONFIG   Debug (default) or Release
#   GEM_TIMEOUT seconds per gem         (default 120)
#   GEM_LOG_DIR where the per-gem logs go
#
# A gem or two takes a few seconds; the whole catalog takes around twenty
# minutes, because the CRuby suites spawn a child interpreter per separated
# test and IronRuby's startup is not free. Name the gems you care about while
# working on one, and raise GEM_TIMEOUT if the box is busy - a gem that runs
# out of time is reported TIMEOUT, not as a failure.

set -u
IR_ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
IR="$IR_ROOT/ir.sh"
RUNNER="$IR_ROOT/Util/gems/runner.rb"
: "${GEM_TIMEOUT:=120}"

# The CRuby suites shell out to "the ruby under test" through EnvUtil, which
# reads $RUBY. Without it they would measure the system CRuby, or hang on it.
export RUBY="$IR"
export RUBY_EXE="$IR"

CRUBY="${CRUBY:-ruby}"

case "${1:-}" in
  popular|stdlib|all) TIER="$1"; shift ;;
  *)                  TIER=stdlib ;;
esac

if [ $# -gt 0 ]; then
  GEMS="$*"
else
  GEMS=$("$IR" "$RUNNER" --list "$TIER" 2>/dev/null)
  [ -z "$GEMS" ] && { echo "could not read the catalog - is the build present?" >&2; exit 1; }
fi

LOG_DIR="${GEM_LOG_DIR:-${TMPDIR:-/tmp}/ir-gems-$$}"
mkdir -p "$LOG_DIR"

fmt='%-15s %-5s %-5s %6s %7s %6s %6s %6s  %s\n'
printf "$fmt" GEM LOADS FROM TESTS ASSERT FAIL ERROR SKIP NOTE
printf '%.0s-' {1..110}; echo

for g in $GEMS; do
  log="$LOG_DIR/$g.log"
  timeout "$GEM_TIMEOUT" "$IR" -X:ObjectSpace "$RUNNER" "$g" >"$log" 2>&1
  rc=$?

  line=$(grep -a '^##GEM##' "$log" | tail -1)
  if [ -z "$line" ]; then
    reason="no result (exit $rc)"
    [ "$rc" = 124 ] && reason="TIMEOUT after ${GEM_TIMEOUT}s"
    printf "$fmt" "$g" "?" - - - - - - "$reason"
    continue
  fi
  IFS=$'\t' read -r _ n loads kind specdir note <<<"$line"

  t=- a=- f=- e=- s=- from=-

  if [ "$kind" = spec ] && [ -d "$IR_ROOT/spec/library/$specdir" ]; then
    # ruby/spec, with this gem's lib/ ahead of IronRuby's own copy.
    inc=$("$IR" "$RUNNER" --load-path "$g" 2>/dev/null)
    timeout "$GEM_TIMEOUT" "$IR" $inc -Imspec/lib mspec/bin/mspec-run \
        "$IR_ROOT/spec/library/$specdir" >>"$log" 2>&1
    rc=$?
    from=spec
    # "12 files, 340 examples, 512 expectations, 0 failures, 0 errors, 0 tagged"
    sum=$(grep -aoE '[0-9]+ files?, [0-9]+ examples?, [0-9]+ expectations?, [0-9]+ failures?, [0-9]+ errors?, [0-9]+ tagged' "$log" | tail -1)
    if [ -n "$sum" ]; then
      nums=($(echo "$sum" | grep -oE '[0-9]+'))
      t=${nums[1]} a=${nums[2]} f=${nums[3]} e=${nums[4]} s=${nums[5]}
    fi
  elif [ "$kind" = exercise ]; then
    # Most popular gems ship no test/ in the .gem. Run the hand-written
    # exercise under IronRuby and under CRuby 4.0.6 and diff: CRuby is the
    # oracle, so a byte-identical stdout is the pass.
    ex="$IR_ROOT/Util/gems/exercises/$specdir.rb"
    from=exer
    timeout "$GEM_TIMEOUT" "$IR" "$ex" >"$LOG_DIR/$g.ir" 2>"$LOG_DIR/$g.ir.err"
    irc=$?
    # The oracle needs the same gems. If CRuby's own gem home does not have
    # them, point CRUBY_GEM_HOME at one that does.
    timeout "$GEM_TIMEOUT" env ${CRUBY_GEM_HOME:+GEM_HOME="$CRUBY_GEM_HOME"} \
      "$CRUBY" "$ex" >"$LOG_DIR/$g.cruby" 2>/dev/null
    crc=$?
    if [ "$crc" != 0 ]; then
      note="the exercise does not run on CRuby either (exit $crc)${note:+; $note}"
    elif [ "$irc" != 0 ]; then
      note="exercise exit $irc: $(grep -a . "$LOG_DIR/$g.ir.err" | tail -1 | cut -c1-90)"
    elif diff -q "$LOG_DIR/$g.ir" "$LOG_DIR/$g.cruby" >/dev/null; then
      t=$(wc -l <"$LOG_DIR/$g.ir"); a=$t; f=0; e=0; s=0
      [ -z "$note" ] && note='matches CRuby 4.0.6'
    else
      t=$(wc -l <"$LOG_DIR/$g.cruby"); f=$(diff "$LOG_DIR/$g.ir" "$LOG_DIR/$g.cruby" | grep -ac '^<'); a=$t; e=0; s=0
      note="differs from CRuby: $(diff "$LOG_DIR/$g.ir" "$LOG_DIR/$g.cruby" | head -4 | tr '\n' ' ' | cut -c1-90)"
    fi
  elif [ "$kind" = unit ]; then
    from=unit
    # "8 tests, 17 assertions, 0 failures, 0 errors, 0 skips" - test/unit and
    # minitest ("runs" for "tests") both end with it.
    sum=$(grep -aoE '[0-9]+ (tests?|runs?), [0-9]+ assertions?, [0-9]+ failures?, [0-9]+ errors?(, [0-9]+ warnings?)?, [0-9]+ skips?' "$log" | tail -1)
    if [ -n "$sum" ]; then
      nums=($(echo "$sum" | grep -oE '[0-9]+'))
      t=${nums[0]} a=${nums[1]} f=${nums[2]} e=${nums[3]} s=${nums[-1]}
    fi
  fi

  if [ "$t" = - ] && [ "$from" != exer ]; then
    if [ "$kind" = none ] || [ "$from" = - ]; then
      [ -z "$note" ] && note='load only (no suite on disk)'
    elif [ "$loads" = ok ]; then
      if [ "$rc" = 124 ]; then note="TIMEOUT after ${GEM_TIMEOUT}s${note:+; $note}"
      else note="suite did not finish (exit $rc)${note:+; $note}"; fi
    fi
  fi
  printf "$fmt" "$n" "$loads" "$from" "$t" "$a" "$f" "$e" "$s" "$note"
done

echo
echo "logs: $LOG_DIR"
