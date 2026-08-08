#!/usr/bin/env bash
# Re-request GitHub Copilot's review on a PR: remove, then re-add, the
# reviewer — a landed review leaves a stale-reviewer state in which a plain
# POST returns 200 and registers nothing (ship.md step 6.1). The endpoint,
# method and body are fixed here so the permission grant covers exactly this
# operation; the PR number is the only variable and it is shape-checked.
set -euo pipefail
pr="${1:?usage: copilot-request.sh <pr-number>}"
[[ "$pr" =~ ^[0-9]+$ ]] || { echo "pr must be a number" >&2; exit 2; }
gh api --method DELETE "repos/{owner}/{repo}/pulls/$pr/requested_reviewers" \
  -f "reviewers[]=copilot-pull-request-reviewer[bot]" --silent 2>/dev/null || true
gh api --method POST "repos/{owner}/{repo}/pulls/$pr/requested_reviewers" \
  -f "reviewers[]=copilot-pull-request-reviewer[bot]" --silent
