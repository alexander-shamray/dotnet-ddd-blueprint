#!/usr/bin/env bash
# Count review_requested events on a PR's timeline — the only proof a
# Copilot request registered, since the request endpoint returns 200 either
# way (ship.md step 6.1). Read-only, fixed endpoint.
set -euo pipefail
pr="${1:?usage: copilot-request-count.sh <pr-number>}"
[[ "$pr" =~ ^[0-9]+$ ]] || { echo "pr must be a number" >&2; exit 2; }
gh api "repos/{owner}/{repo}/issues/$pr/timeline" --paginate \
  --jq '[.[] | select(.event=="review_requested")] | length'
