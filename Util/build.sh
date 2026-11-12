#!/bin/sh
# Build the console with the shared DLR source tree used by this worktree set.
IR_ROOT="$(dirname "$(dirname "$(readlink -f "$0")")")"
export DOTNET_ROOT=/usr/local/dotnet
export PATH=/usr/local/dotnet:$PATH
exec dotnet build "$IR_ROOT/Src/Console/Ruby.Console.csproj" -p:DlrSourceDir=/root/workspace/dlr-agents/marshal "$@"
