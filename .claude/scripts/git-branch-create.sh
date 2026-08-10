#!/usr/bin/env bash
# Create the branch /branch makes when it is NOT forking a worktree, and
# nothing else. Two bases, spelled as literals, because step 5's table has
# exactly two:
#
#   origin/main   the clean-`main` rows — already in a linked worktree, or the
#                 parent is not writable. --no-track travels with it, since the
#                 start point is a remote-tracking ref and /pr must be the one
#                 to set the upstream.
#   HEAD          the dirty and detached rows, whose whole point is carrying
#                 what is already in this tree.
#
# A `Bash(git checkout -b:*)` grant buys those and also the trailing flag:
# `git checkout -b <name> -f origin/main` was run against a throwaway clone and
# was accepted, so the grant licenses discarding tracked modifications — on the
# very path whose purpose is to carry them, and past the `git reset --hard` and
# `git clean` denies. Prefix rules cannot exclude a flag, which is the same
# conclusion git-switch-existing.sh and git-worktree-fork.sh reached.
set -euo pipefail
[ "$#" -eq 2 ] || { echo "usage: git-branch-create.sh <branch> <origin/main|HEAD>" >&2; exit 2; }
branch="$1"
base="$2"
case "$branch" in
  -*) echo "branch name may not start with '-'" >&2; exit 2 ;;
  *..*) echo "branch name may not contain '..'" >&2; exit 2 ;;
esac
[[ "$branch" =~ ^[A-Za-z0-9][A-Za-z0-9._/()-]*$ ]] ||
  { echo "not a branch name this helper will take: $branch" >&2; exit 2; }
# Creation only, like the fork helper: a name that already exists is refused
# rather than reset, so no caller can reach -B/-B-like behaviour through here.
! git show-ref --verify --quiet "refs/heads/$branch" ||
  { echo "branch already exists: $branch" >&2; exit 3; }
case "$base" in
  origin/main)
    git show-ref --verify --quiet refs/remotes/origin/main ||
      { echo "no refs/remotes/origin/main — fetch first (step 1)" >&2; exit 4; }
    git checkout -b "$branch" --no-track origin/main
    ;;
  HEAD)
    git checkout -b "$branch"
    ;;
  *)
    echo "base must be origin/main or HEAD, not: $base" >&2; exit 2 ;;
esac
