#!/usr/bin/env bash
# Remove a worktree this repository registered, without -f.
#
# The missing flag is the point. /security-sweep's own prose leans on git
# refusing to remove a checkout that holds anything modified or untracked —
# "if `git worktree remove` refuses, leave the worktree standing and report
# what it is holding" — and `git worktree remove -f` defeats exactly that
# guard, which a `Bash(git worktree remove:*)` grant would license. Same shape
# as the other helpers here: the flag is decided in the file, not by a caller.
#
# It also refuses the main worktree, so a mistyped argument cannot aim this at
# the checkout the session is standing in.
set -euo pipefail
[ "$#" -eq 1 ] || { echo "usage: git-worktree-drop.sh <path>" >&2; exit 2; }
path="$1"
case "$path" in -*) echo "path may not start with '-'" >&2; exit 2 ;; esac
[ -d "$path" ] || { echo "not an existing directory: $path" >&2; exit 2; }
# Resolve both sides before comparing: `git worktree list` prints absolute
# paths, and the caller holds whatever spelling mktemp gave it.
resolved=$(cd "$path" && pwd -P)
main=$(git worktree list --porcelain | awk 'NR==1 && $1=="worktree" {print $2; exit}')
main_resolved=$(cd "$main" && pwd -P)
[ "$resolved" != "$main_resolved" ] ||
  { echo "refusing to remove the main worktree: $path" >&2; exit 3; }
git worktree list --porcelain |
  awk -v t="$resolved" '$1=="worktree" {print $2}' |
  while read -r w; do (cd "$w" 2>/dev/null && pwd -P); done |
  grep -Fxq "$resolved" ||
  { echo "not a worktree this repository registered: $path" >&2; exit 3; }
git worktree remove "$resolved"
