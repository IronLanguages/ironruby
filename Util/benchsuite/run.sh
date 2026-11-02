#!/bin/bash
# Rerunnable speed + compatibility suite: the benchmarks and tests TruffleRuby
# measures itself with, run under IronRuby and CRuby side by side.
#
#   Util/benchsuite/run.sh --fetch               # clone the sources (once)
#   Util/benchsuite/run.sh                       # truffle + awfy timings
#   Util/benchsuite/run.sh --verify              # truffle + awfy answers
#   Util/benchsuite/run.sh --suite mri -j 6      # CRuby's test/ruby
#   Util/benchsuite/run.sh --all                 # all three, then ruby-bench micro
#   Util/benchsuite/run.sh --suite awfy --baseline /tmp/benchsuite-<older>
#
# ruby-bench (formerly yjit-bench) has its own runners: Util/rubybench/run.sh
# for the micro group, which --all calls after the suites here, and
# Util/rubybench/pure-gems-run.sh for the gem-driven macro benchmarks
# (optcarrot among them), which bundle-installs into a ruby-bench copy.
#
# IR_CONFIG defaults to Release here (a Debug build is ~1.8x slower and
# would make every ratio meaningless). Everything else is in suite.rb --help
# and README.md.

set -eu
HERE="$(dirname "$(readlink -f "$0")")"
ROOT="$(dirname "$(dirname "$HERE")")"
: "${BS_SRC:=/root/workspace/bench-src}"
: "${IR_CONFIG:=Release}"
: "${RUBY:=ruby}"
export BS_SRC IR_CONFIG RUBY

# Pinned so two runs months apart measure the same code. Bump deliberately
# (and re-run --verify, since a newer bench/ can add or change benchmarks).
TRUFFLE_REPO=https://github.com/oracle/truffleruby.git
TRUFFLE_REV=${TRUFFLE_REV:-6f6733a5a4c09c921d68b150c6692a456fd6c457}   # 2026-10-28
AWFY_REPO=https://github.com/smarr/are-we-fast-yet.git
AWFY_REV=${AWFY_REV:-74306fec151070fd07157cefeacf19e7e0bcdc89}         # 2026-03-22

checkout() {  # repo dir rev [sparse-dir]
  if [ ! -d "$2/.git" ]; then
    git init -q "$2"
    git -C "$2" remote add origin "$1"
    if [ -n "${4:-}" ]; then
      git -C "$2" config core.sparseCheckout true
      git -C "$2" sparse-checkout set "$4"
    fi
  fi
  git -C "$2" fetch -q --depth 1 --filter=blob:none origin "$3"
  git -C "$2" checkout -q --detach FETCH_HEAD
}

fetch() {
  mkdir -p "$BS_SRC"
  checkout "$TRUFFLE_REPO" "$BS_SRC/truffleruby" "$TRUFFLE_REV" bench
  checkout "$AWFY_REPO" "$BS_SRC/are-we-fast-yet" "$AWFY_REV"
  for d in truffleruby are-we-fast-yet; do
    printf '%-16s %s\n' "$d" "$(git -C "$BS_SRC/$d" log -1 --format='%h %cs %s')"
  done
}

all=0
args=()
for a in "$@"; do
  case "$a" in
    --fetch) fetch; exit 0 ;;
    --all) all=1 ;;
    *) args+=("$a") ;;
  esac
done

bin="$ROOT/Src/Console/bin/$IR_CONFIG/${IR_TFM:-net8.0}/ir"
if [ ! -x "$bin" ]; then
  echo "no $IR_CONFIG build at $bin - build it first:" >&2
  echo "  dotnet build Src/Console/Ruby.Console.csproj -c $IR_CONFIG -p:DlrSourceDir=/root/workspace/dlr/src/core" >&2
  exit 1
fi

if [ "$all" = 1 ]; then
  "$RUBY" "$HERE/suite.rb" --suite truffle,awfy,mri ${args[@]+"${args[@]}"}
  rb_args=(--group micro)
  for a in ${args[@]+"${args[@]}"}; do [ "$a" = --verify ] && rb_args+=(--verify); done
  "$ROOT/Util/rubybench/run.sh" "${rb_args[@]}"
else
  exec "$RUBY" "$HERE/suite.rb" ${args[@]+"${args[@]}"}
fi
