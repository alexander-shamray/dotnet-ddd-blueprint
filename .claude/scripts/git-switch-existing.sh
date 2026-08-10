#!/usr/bin/env bash
# Switch the current checkout to an EXISTING local branch, and nothing else.
#
# /branch step 5 needs this on one path: `git worktree add -b` creates the
# branch before the directory, so a fork that fails on an unwritable parent
# leaves the branch behind, and the in-place fallback has to get onto it. A
# plain `Bash(git switch:*)` grant would buy that one operation and also
# license `--discard-changes` and `-C` — discarding work and force-moving a
# branch, both of which .claude/settings.json denies in their other spellings
# (`git reset --hard`, `git clean`, `git branch -D/-M`).
#
# A deny list cannot close that, which is why this file exists rather than
# three more deny rules: the flags COMBINE. `git switch -fC <name> <start>`
# was run against a throwaway clone and switched, so `Bash(git switch -C:*)`
# matches nothing of it. Same shape as the refspec argument /pr already makes
# about pushes — a prefix rule cannot enumerate the spellings.
#
# So: no flags reach git. `--` ends option parsing (checked: `git switch --
# --discard-changes` reports `fatal: invalid reference`, it does not discard),
# the name is shape-checked before it is used, and the branch must already
# exist — this helper never creates one. Read-only as to history; the only
# state it changes is which branch is checked out.
set -euo pipefail
branch="${1:?usage: git-switch-existing.sh <branch>}"
[ "$#" -eq 1 ] || { echo "exactly one argument: the branch name" >&2; exit 2; }
# Branch names here are <type>/<kebab>, and feat(scope)/ carries parentheses.
# Leading '-' is refused outright so nothing can arrive looking like a flag,
# and '..' is refused because a refname may not contain it.
case "$branch" in
  -*) echo "branch name may not start with '-'" >&2; exit 2 ;;
  *..*) echo "branch name may not contain '..'" >&2; exit 2 ;;
esac
[[ "$branch" =~ ^[A-Za-z0-9][A-Za-z0-9._/()-]*$ ]] ||
  { echo "not a branch name this helper will take: $branch" >&2; exit 2; }
# It must already exist as a LOCAL branch. Without this the helper would
# happily detach onto a tag or a commit that satisfied the pattern above.
git show-ref --verify --quiet "refs/heads/$branch" ||
  { echo "no such local branch: $branch" >&2; exit 3; }
git switch -- "$branch"
