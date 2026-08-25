#!/usr/bin/env bash
# List the pull requests for one branch — number, state, url — and nothing
# else. Read-only, fixed field set.
#
# **Exists because `gh pr list` reaches the review feeds (#56).** Removing
# `Bash(gh pr view:*)` from the three commands that held it was not enough:
# `gh pr list --json reviews,comments` returns the same review bodies and issue
# comments, in full, for every pull request at once. Measured, not reasoned —
# `gh pr list --state all --limit 1 --json number,reviews` on this repository
# returned a 2,457-character Copilot review body. So the grant those commands
# kept for the harmless job of finding a branch's PR was a complete bypass of
# all three author-filtering helpers.
#
# That is the same defect as the `gh pr view` one, one subcommand over, and it
# is why the test that pins this invariant now enumerates the *commands that
# can reach the fields* rather than the one spelling that was fixed first.
#
# The branch is optional and defaults to the checkout's current branch. When
# given it is shape-checked, because it reaches an argument position: a value
# starting with `-` would be read as a flag, and `gh pr list` has flags that
# change what is returned.
set -euo pipefail
branch="${1:-}"
if [ -z "$branch" ]; then
  branch=$(git branch --show-current)
  [ -n "$branch" ] || { echo "detached HEAD and no branch given" >&2; exit 2; }
fi
case "$branch" in
  -*) echo "branch name may not start with '-'" >&2; exit 2 ;;
esac
[[ "$branch" =~ ^[A-Za-z0-9][A-Za-z0-9._/()-]*$ ]] ||
  { echo "not a branch name this helper will take: $branch" >&2; exit 2; }
gh pr list --state all --head "$branch" --json number,state,url
