#!/usr/bin/env bash
# Read a PR's merge-relevant state as JSON — /ship's only `gh pr view` route.
# Read-only, fixed field set.
#
# **Exists so that ship.md need not hold `Bash(gh pr view:*)` (#56).** That
# grant was the last unfiltered way to reach `--json reviews` and
# `--json comments`: review-copilot.md dropped it and routed those two feeds
# through filtering helpers, but /ship invokes /review-copilot as a skill while
# holding its own broader grant, and `allowed-tools` entries are cumulative
# auto-approvals rather than a whitelist. So the narrower grant one file over
# withheld nothing on the unattended path — which is the path #56 was filed
# about. Removing the broad grant is what makes the filter reach it.
#
# Whether a skill invocation actually inherits the caller's frontmatter grants
# has never been measured here. This helper makes that question moot rather
# than answering it: with no `gh pr view` grant in either file, neither
# inheritance rule leaves an unfiltered route.
#
# The field set is the union of the three reads ship.md performs — the resume
# table's `state`, step 7's pre-merge check, and step 7's post-merge
# confirmation. One fixed set rather than a parameter, for the reason every
# helper here fixes its endpoint: a caller that chooses fields can choose
# `reviews`.
set -euo pipefail
pr="${1:?usage: pr-state.sh <pr-number>}"
[[ "$pr" =~ ^[0-9]+$ ]] || { echo "pr must be a number" >&2; exit 2; }
gh pr view "$pr" --json state,mergeable,mergeStateStatus,headRefOid,mergeCommit
