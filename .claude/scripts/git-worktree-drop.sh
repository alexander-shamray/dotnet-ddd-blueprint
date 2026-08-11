#!/usr/bin/env bash
# Remove the throwaway worktree a sweep created — /security-sweep or
# /bug-sweep — without -f.
#
# The missing flag is half the point. A sweep's teardown leans on git
# refusing to remove a checkout holding anything modified or untracked — "if
# `git worktree remove` refuses, leave the worktree standing and report what it
# is holding" — and `git worktree remove -f` defeats exactly that guard, which
# a `Bash(git worktree remove:*)` grant would license.
#
# The other half is WHICH path. Registration is not ownership: every sibling PR
# worktree is registered too, and the audited tree is prompt-injection input, so
# a path arriving here may have been chosen by it. Accepting any non-main
# worktree would let a poisoned finding steer this at someone else's clean
# workspace. So the path must match `secsweep-` plus six characters under the
# canonical temp root.
#
# That EXCLUDES sibling PR worktrees and anything outside the temp root, which
# is what it is for. It does NOT prove a sweep created the path — `mktemp` takes
# an arbitrary template, and this helper accepts any registered worktree of the
# right shape — and it is NOT strictly a direct-child check, because `?` matches
# `/` in a bash `case` (no pathname expansion), so $tmproot/secsweep-a/bbbb
# passes. Verified by running both through a `case`. Fix owed, one line:
# compare `dirname "$resolved"` against "$tmproot" and match the basename
# alone; git-worktree-detach.sh owes the same.
set -euo pipefail
[ "$#" -eq 1 ] || { echo "usage: git-worktree-drop.sh <path>" >&2; exit 2; }
path="$1"
case "$path" in -*) echo "path may not start with '-'" >&2; exit 2 ;; esac
[ -d "$path" ] || { echo "not an existing directory: $path" >&2; exit 2; }
tmproot=$(cd "${TMPDIR:-/tmp}" 2>/dev/null && pwd -P) ||
  { echo "cannot resolve the temp root" >&2; exit 4; }
resolved=$(cd "$path" && pwd -P)
case "$resolved" in
  "$tmproot"/secsweep-??????) : ;;
  *) echo "not a sweep-shaped temp path: $path" >&2; exit 2 ;;
esac
# Ask git whether this is a linked worktree of THIS repository, rather than
# comparing path strings against `git worktree list`. Under MSYS those strings
# are not comparable at all — git prints `C:/Users/…/Temp/x` where `pwd -P`
# prints `/tmp/x`, and a textual check refuses the sweep's own worktree on the
# host this repository is developed on. Both values below come from git in one
# format, so they compare.
common_here=$(git rev-parse --path-format=absolute --git-common-dir)
common_there=$(git -C "$path" rev-parse --path-format=absolute --git-common-dir 2>/dev/null) ||
  { echo "not a git worktree: $path" >&2; exit 3; }
[ "$common_there" = "$common_here" ] ||
  { echo "not a worktree of this repository: $path" >&2; exit 3; }
dir_there=$(git -C "$path" rev-parse --path-format=absolute --git-dir)
[ "$dir_there" != "$common_there" ] ||
  { echo "refusing to remove the main worktree: $path" >&2; exit 3; }
git worktree remove "$resolved"
