#!/bin/bash
# Installs the popular tier of Util/gems/catalog.rb from rubygems.org, with
# IronRuby's own RubyGems (./igem.sh), one gem per process.
#
#   export GEM_HOME="$PWD/.gems"      # do NOT share the user gem dir: several
#   Util/gems/install-popular.sh      # agents write to it at once and RubyGems
#   Util/gems/install-popular.sh rack # does not survive that
#
# A gem that is already there is skipped, so re-running it is cheap. A gem with
# a C extension fails here, at the compile step, and that failure is the
# finding - it is recorded in Util/gems/POPULAR.md, not worked around.
#
# Environment:
#   GEM_INSTALL_TIMEOUT  seconds per gem (default 300)
#   GEM_LOG_DIR          where the per-gem install logs go

set -u
IR_ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
: "${GEM_INSTALL_TIMEOUT:=300}"

if [ -z "${GEM_HOME:-}" ]; then
  echo "set GEM_HOME first - the shared user gem dir is not safe here" >&2
  exit 1
fi

LOG_DIR="${GEM_LOG_DIR:-${TMPDIR:-/tmp}/ir-gem-install-$$}"
mkdir -p "$LOG_DIR"

if [ $# -gt 0 ]; then
  GEMS="$*"
else
  GEMS=$("$IR_ROOT/ir.sh" "$IR_ROOT/Util/gems/runner.rb" --list popular 2>/dev/null)
  [ -z "$GEMS" ] && { echo "could not read the catalog - is the build present?" >&2; exit 1; }
fi

for g in $GEMS; do
  if ls "$GEM_HOME/gems" 2>/dev/null | grep -q "^$g-[0-9]"; then
    printf '%-24s %s\n' "$g" 'already installed'
    continue
  fi
  if timeout "$GEM_INSTALL_TIMEOUT" "$IR_ROOT/igem.sh" install --no-document "$g" \
       >"$LOG_DIR/$g.log" 2>&1; then
    printf '%-24s %s\n' "$g" "$(grep -aoE 'Successfully installed .*' "$LOG_DIR/$g.log" | tail -1)"
  else
    printf '%-24s FAILED: %s\n' "$g" \
      "$(grep -aE 'ERROR|error:' "$LOG_DIR/$g.log" | head -1 | cut -c1-120)"
  fi
done

echo
echo "install logs: $LOG_DIR"
