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
# **`--head` filters on the branch NAME and nothing else**, which is not the
# question being asked. It matches across forks, so an outside contributor's
# pull request from a same-named branch is a candidate here — and /ship step 0
# reads this to decide whether the branch landed, /pr to decide whether one is
# already open. Acting on a stranger's pull request is the failure.
#
# grok-review.sh:139 already carries this check and the argument for it, and
# this helper shipped without it: a fix that closes a hole by name and leaves
# it open by provenance has moved the defect rather than removed it — written
# down one file away, in a comment, and reimplemented wrong here anyway.
#
# Both sides of the comparison are properties of the filesystem: `gh repo view`
# reads the checkout, and the branch came from `git branch --show-current` or
# was shape-checked above. The value reaches jq through `--arg`, never as
# program text.
repo=$(gh repo view --json nameWithOwner --jq .nameWithOwner) ||
  { echo "cannot resolve this checkout's repository" >&2; exit 2; }
# **Blank counts as missing, and `||` does not see it.** `gh` printing an empty
# string exits 0, so the guard above passes and `$repo` is "" — and the
# comparison below then matches every row whose head repository is absent,
# which `// ""` renders as "" too. A deleted fork reports exactly that. So the
# filter would admit a stranger's pull request precisely when it could not
# establish whose it was, which is the fail-open direction.
[ -n "$repo" ] ||
  { echo "this checkout's repository resolved to nothing" >&2; exit 2; }
gh pr list --state all --head "$branch" --json number,state,url,headRepository |
  jq --arg repo "$repo" \
    '[ .[] | select((.headRepository.nameWithOwner // "") == $repo)
       | {number, state, url} ]'
