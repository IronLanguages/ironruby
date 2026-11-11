#!/bin/bash
# SessionStart hook for Claude Code cloud sessions (claude.ai/code).
#
# Prepares what building IronRuby needs next to the checkout, as .github/workflows/ci.yml
# does: ../dlr at DLR_REF with Build/dlr-patches applied, ../prism at PRISM_REF with
# libprism built, and the native libraries for markly and rbs. The system toolchain
# (.NET 11 SDK + .NET 10 runtime, Ruby 4) comes from the environment's setup script.
#
# Each step leaves a stamp in the directory it fills and runs again only when the stamp no
# longer matches (a new ref or patch in ci.yml, a changed build script, a new container).
set -euo pipefail

if [ "${CLAUDE_CODE_REMOTE:-}" != "true" ]; then
  exit 0
fi

SELF=$(readlink -f "$0")
IR="${CLAUDE_PROJECT_DIR:-$(cd "$(dirname "$0")/../.." && pwd)}"
PARENT=$(dirname "$IR")
DLR="$PARENT/dlr"
PRISM="$PARENT/prism"
cd "$IR"

ci_env() { sed -n "s/^ *$1: *//p" .github/workflows/ci.yml | head -n 1; }
DLR_REF=$(ci_env DLR_REF)
PRISM_REF=$(ci_env PRISM_REF)
[ -n "$DLR_REF" ] && [ -n "$PRISM_REF" ] || { echo "session-start: no DLR_REF/PRISM_REF in ci.yml" >&2; exit 1; }

# fresh <file> <inputs...>: succeeds when <file> holds the hash of the inputs and this script;
# stamp <file> <inputs...> writes it.
digest() { { printf '%s\n' "$@"; cat "$SELF"; } | sha256sum | cut -d' ' -f1; }
fresh() { local f=$1; shift; [ -f "$f" ] && [ "$(cat "$f")" = "$(digest "$@")" ]; }
stamp() { local f=$1; shift; digest "$@" > "$f"; }

# Check out <dir> at <ref> of <url>, fetching just that commit.
checkout() {
  local dir=$1 url=$2 ref=$3
  [ -d "$dir/.git" ] || git init -q "$dir"
  if ! git -C "$dir" cat-file -e "$ref^{commit}" 2>/dev/null; then
    git -C "$dir" fetch -q --depth 1 "$url" "$ref"
  fi
  git -C "$dir" checkout -q -f --detach "$ref"
  git -C "$dir" clean -q -fdx
}

# --- Ruby 4 as `ruby` (fallback if the setup script did not relink /usr/local/bin) -------
if [ -x /opt/ruby-4/bin/ruby ] && ! ruby -e 'exit RUBY_VERSION.start_with?("4.")' 2>/dev/null; then
  for exe in /opt/ruby-4/bin/*; do ln -sfn "$exe" "/usr/local/bin/$(basename "$exe")"; done
  hash -r
fi

# --- DLR -----------------------------------------------------------------------------------
dlr_inputs=("$DLR_REF" "$(cat Build/dlr-patches/*.patch | sha256sum)")
if ! fresh "$DLR/.session-start.sha256" "${dlr_inputs[@]}"; then
  echo "session-start: DLR at $DLR_REF"
  checkout "$DLR" https://github.com/IronLanguages/dlr "$DLR_REF"
  for p in Build/dlr-patches/*.patch; do git -C "$DLR" apply "$IR/$p"; done
  stamp "$DLR/.session-start.sha256" "${dlr_inputs[@]}"
  # the stamp is inside the checkout: keep it out of the DLR's git status
  echo ".session-start.sha256" >> "$DLR/.git/info/exclude"
fi

# --- prism / libprism ------------------------------------------------------------------------
if ! fresh "$PRISM/build/.session-start.sha256" "$PRISM_REF" || [ ! -f "$PRISM/build/libprism.so" ]; then
  echo "session-start: prism at $PRISM_REF"
  checkout "$PRISM" https://github.com/ruby/prism "$PRISM_REF"
  (cd "$PRISM" && ruby templates/template.rb && make shared -j"$(nproc)")
  stamp "$PRISM/build/.session-start.sha256" "$PRISM_REF"
fi

# --- markly (cmark-gfm) and librbs -----------------------------------------------------------
markly_out=Src/Libraries/Markly/native/build
markly_inputs=("$(cat Util/build-markly.sh $(find Src/Libraries/Markly/native -maxdepth 1 -type f | sort) | sha256sum)")
if ! fresh "$markly_out/.session-start.sha256" "${markly_inputs[@]}"; then
  echo "session-start: building libmarkly_cmark"
  bash Util/build-markly.sh
  stamp "$markly_out/.session-start.sha256" "${markly_inputs[@]}"
fi

rbs_out=Src/Libraries/Rbs/native/build
rbs_inputs=("$(cat Util/build-librbs.sh $(find Src/Libraries/Rbs/native -maxdepth 1 -type f | sort) | sha256sum)")
if ! fresh "$rbs_out/.session-start.sha256" "${rbs_inputs[@]}"; then
  echo "session-start: building librbs"
  bash Util/build-librbs.sh
  stamp "$rbs_out/.session-start.sha256" "${rbs_inputs[@]}"
fi

# --- Session environment ---------------------------------------------------------------------
if [ -n "${CLAUDE_ENV_FILE:-}" ]; then
  # Apphosts (ironruby, ir) find the runtime through DOTNET_ROOT; point it at the real install.
  dotnet_root=$(dirname "$(readlink -f "$(command -v dotnet)")")
  {
    echo "export DOTNET_ROOT=\"$dotnet_root\""
    echo "export DLR_SOURCE_DIR=\"$DLR/src/core\""
  } >> "$CLAUDE_ENV_FILE"
fi
