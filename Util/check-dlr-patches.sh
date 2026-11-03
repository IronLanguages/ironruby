#!/bin/sh
# Checks that a DLR checkout has every patch in Build/dlr-patches applied:
#
#   Util/check-dlr-patches.sh ../dlr/src/core   # any directory in the DLR checkout
#
# CI applies them to a fresh checkout every time; a local one is patched once and then
# falls behind when a new patch lands, and builds against it without a word. Exits 1 and
# names the newest patch missing; exits 0 when all are there, or when the directory is not
# in a git checkout, where there is no tree to check.
#
# A later patch can change the lines next to an earlier one, so a patch is not checked on
# its own: the series is taken back off, newest first, from a copy of the files it touches.
IR_ROOT="$(dirname "$(dirname "$(readlink -f "$0")")")"
dlr_root=$(git -C "${1:?usage: Util/check-dlr-patches.sh DLR_DIR}" rev-parse --show-toplevel 2>/dev/null) || exit 0
check=$(mktemp -d)
trap 'rm -rf "$check"' EXIT
# git apply in a repository's subdirectory skips the patch's paths outside it, and then
# "succeeds" without checking anything: the caller's directory, or a TMPDIR inside a checkout.
# So the copy is a repository of its own, and every git apply runs at its top.
git init -q "$check"
for p in "$IR_ROOT"/Build/dlr-patches/*.patch; do
  (cd "$check" && git apply --numstat "$p") | cut -f3
done | sort -u | while read -r f; do
  [ -f "$dlr_root/$f" ] && mkdir -p "$check/$(dirname "$f")" && cp "$dlr_root/$f" "$check/$f"
done
for p in $(cd "$IR_ROOT" && ls -r Build/dlr-patches/*.patch); do
  (cd "$check" && git apply --reverse "$IR_ROOT/$p" 2>/dev/null) || {
    echo "check-dlr-patches: $dlr_root does not have all of Build/dlr-patches ($(basename "$p") is not applied);" >&2
    echo "check-dlr-patches: apply them, or use a DLR that has them (see Build/dlr-patches/README.md)" >&2
    exit 1
  }
done
