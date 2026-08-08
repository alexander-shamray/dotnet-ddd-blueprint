#!/usr/bin/env bash
# Reply to one inline review comment — the reasoned replies and the one-word
# markers of /review-copilot. The endpoint is fixed, so the grant covers
# posting a reply on this repository's PR threads and nothing else; both ids
# are shape-checked.
set -euo pipefail
pr="${1:?usage: pr-comment-reply.sh <pr-number> <comment-id> <body>}"
cid="${2:?usage: pr-comment-reply.sh <pr-number> <comment-id> <body>}"
body="${3:?usage: pr-comment-reply.sh <pr-number> <comment-id> <body>}"
[[ "$pr" =~ ^[0-9]+$ && "$cid" =~ ^[0-9]+$ ]] || { echo "ids must be numbers" >&2; exit 2; }
gh api "repos/{owner}/{repo}/pulls/$pr/comments/$cid/replies" -f body="$body" --jq .id
