#!/usr/bin/env bash
# `dotnet test` for /review-branch, with no free parameter anywhere.
#
# It replaces `Bash(dotnet test:*)` and `Bash(dotnet build:*)`, both of which
# were grants wider than the operation the command names — and the second was
# never used by it at all. A trailing argument chooses which project to build,
# and an MSBuild property chooses a file to IMPORT:
# `/p:CustomBeforeMicrosoftCommonTargets=<path>` executes whatever XML it is
# pointed at, including a file this command may legitimately write. So the
# solution, the filter and the flag set are all fixed here, and the only
# variable is one word out of two.
#
# **This closes the executor and not the import.** A `Directory.Build.targets`
# at the repository root is auto-imported by any build of any project beneath
# it — measured, an `Exec` in one runs during `dotnet build` — so the file
# itself has to be out of reach, which is `disallowed-tools`' job in
# review-branch.md rather than this script's.
set -euo pipefail

mode="${1:-all}"
root="$(git rev-parse --show-toplevel)"
cd "$root"

case "$mode" in
  all)
    exec dotnet test Platform.slnx
    ;;
  fast)
    exec dotnet test Platform.slnx --filter "Category!=Integration"
    ;;
  *)
    echo "usage: dotnet-test.sh [all|fast]" >&2
    exit 2
    ;;
esac
