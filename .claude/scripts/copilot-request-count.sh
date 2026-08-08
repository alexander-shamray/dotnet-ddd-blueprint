#!/usr/bin/env bash
# Count Copilot review requests on a PR's timeline — the only proof a
# request registered, since the request endpoint returns 200 either way
# (ship.md step 6.1). Pages are slurped before counting: with --paginate,
# a per-page --jq emits one number per page, and a busy PR would hand the
# loop several counts where it expects one. Filtered to Copilot's login so
# another reviewer's request cannot stand in as proof. Read-only.
set -euo pipefail
pr="${1:?usage: copilot-request-count.sh <pr-number>}"
[[ "$pr" =~ ^[0-9]+$ ]] || { echo "pr must be a number" >&2; exit 2; }
gh api "repos/{owner}/{repo}/issues/$pr/timeline" --paginate |
  jq -s 'add | map(select(.event == "review_requested" and (.requested_reviewer.login // "") == "Copilot")) | length'
