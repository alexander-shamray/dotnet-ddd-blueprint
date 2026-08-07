#!/usr/bin/env bash
# Resolve one review thread on one of this repository's PRs. PRRT node ids
# are global and the mutation runs wherever the token reaches, so a shape
# check alone would let this helper resolve a thread on someone else's
# repository — membership in the named PR's own thread map is verified
# first, through the paginated helper. Idempotent: resolving a resolved
# thread returns true unchanged.
set -euo pipefail
pr="${1:?usage: pr-thread-resolve.sh <pr-number> <PRRT-thread-id>}"
tid="${2:?usage: pr-thread-resolve.sh <pr-number> <PRRT-thread-id>}"
[[ "$pr" =~ ^[0-9]+$ ]] || { echo "pr must be a number" >&2; exit 2; }
[[ "$tid" =~ ^PRRT_[A-Za-z0-9_-]+$ ]] || { echo "not a review-thread id" >&2; exit 2; }
script_dir=$(dirname "${BASH_SOURCE[0]}")
bash "$script_dir/pr-review-threads.sh" "$pr" | cut -d' ' -f1 | grep -qx "$tid" ||
  { echo "thread $tid is not on PR $pr of this repository" >&2; exit 3; }
gh api graphql -f query='mutation($id:ID!){
  resolveReviewThread(input:{threadId:$id}){ thread{ isResolved } }
}' -F id="$tid" --jq '.data.resolveReviewThread.thread.isResolved'
