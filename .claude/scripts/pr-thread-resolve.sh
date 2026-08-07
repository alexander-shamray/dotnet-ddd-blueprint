#!/usr/bin/env bash
# Resolve one review thread. The mutation text is fixed and the thread id is
# shape-checked, so the grant covers exactly this write — not arbitrary
# GraphQL. Idempotent: resolving a resolved thread returns true unchanged.
set -euo pipefail
tid="${1:?usage: pr-thread-resolve.sh <PRRT-thread-id>}"
[[ "$tid" =~ ^PRRT_[A-Za-z0-9_-]+$ ]] || { echo "not a review-thread id" >&2; exit 2; }
gh api graphql -f query='mutation($id:ID!){
  resolveReviewThread(input:{threadId:$id}){ thread{ isResolved } }
}' -F id="$tid" --jq '.data.resolveReviewThread.thread.isResolved'
