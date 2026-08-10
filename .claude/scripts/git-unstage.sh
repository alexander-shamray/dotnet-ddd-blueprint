#!/usr/bin/env bash
# Unstage paths, and nothing else: `git reset HEAD -- <pathspec>...`.
#
# This exists because narrowing the grant did not work, and the failure is
# worth keeping: `Bash(git reset HEAD:*)` obviously admits
# `git reset HEAD --hard`, so it was narrowed to `Bash(git reset HEAD --:*)`
# on the reasoning that `--` turns a later flag into a pathspec. It does — but
# the RULE is a prefix match, and `git reset HEAD --hard` starts with
# `git reset HEAD --`. The narrowed grant admitted the exact command it was
# written to exclude, and the deny (`git reset --hard`) matches only the other
# word order. Prose said the hole was closed while it was open.
#
# A prefix rule cannot say "and then a space". A helper can: the separator is
# written here, every argument lands after it, and no argument may begin with
# '-' — so `--hard` cannot arrive as a flag by any route.
set -euo pipefail
[ "$#" -ge 1 ] || { echo "usage: git-unstage.sh <path>..." >&2; exit 2; }
for p in "$@"; do
  case "$p" in
    -*) echo "path may not start with '-': $p" >&2; exit 2 ;;
    *) : ;;
  esac
done
git reset HEAD -- "$@"
