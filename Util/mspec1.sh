#!/bin/sh
# Run a single spec file under mspec with the built ir, the way the sweep does.
IR_ROOT="$(dirname "$(dirname "$(readlink -f "$0")")")"
cd "$IR_ROOT" || exit 1
RUBY_EXE="$IR_ROOT/ir.sh"
export RUBY_EXE
exec timeout -s KILL "${SWEEP_TIMEOUT:-60}" "$IR_ROOT/ir.sh" -Imspec/lib mspec/bin/mspec-run "$@"
