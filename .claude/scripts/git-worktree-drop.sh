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
# is what it is for.
#
# It is a **direct-child** check now, and it was not before: the old form was one
# `case "$resolved" in "$tmproot"/secsweep-??????)`, and a bash `case` does no
# pathname expansion, so `?` matches `/` like any other character and
# `$tmproot/secsweep-a/bbbb` passed as readily as `$tmproot/secsweep-abc123`.
# Verified by running both through a `case`, with wrong-length, wrong-prefix and
# wrong-root controls all correctly refused — prefix and length held,
# direct-childness did not. Comparing `dirname` against the root and matching
# the basename alone is what fixes it, because a basename contains no `/`.
#
# It now also DOES mean a sweep created the path, which the old comment
# correctly said it did not. `git-worktree-detach.sh` creates the directory
# itself and prints it, so no caller supplies a path to either helper and the
# `Bash(mktemp:*)` grant that made an arbitrary template reachable is gone from
# both sweeps. The shape is still checked here rather than assumed: a helper
# whose guard holds only while the other helper is unedited has no guard.
set -euo pipefail
[ "$#" -eq 1 ] || { echo "usage: git-worktree-drop.sh <path>" >&2; exit 2; }
path="$1"
case "$path" in -*) echo "path may not start with '-'" >&2; exit 2 ;; esac
[ -d "$path" ] || { echo "not an existing directory: $path" >&2; exit 2; }
tmproot=$(cd "${TMPDIR:-/tmp}" 2>/dev/null && pwd -P) ||
  { echo "cannot resolve the temp root" >&2; exit 4; }
resolved=$(cd "$path" && pwd -P)
[ "$(dirname "$resolved")" = "$tmproot" ] ||
  { echo "not a direct child of the temp root: $path" >&2; exit 2; }
case "$(basename "$resolved")" in
  secsweep-??????) : ;;
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
