#!/usr/bin/env bash
# Map a PR's review threads: thread id, resolved state, first comment's
# database id and path — the join /review-copilot needs, because resolution
# is a GraphQL mutation on the thread id, not the comment id. Read-only; the
# query text is fixed here.
set -euo pipefail
pr="${1:?usage: pr-review-threads.sh <pr-number>}"
[[ "$pr" =~ ^[0-9]+$ ]] || { echo "pr must be a number" >&2; exit 2; }
owner=$(gh repo view --json owner --jq .owner.login)
repo=$(gh repo view --json name --jq .name)
gh api graphql -f query='
query($owner:String!,$repo:String!,$pr:Int!){
  repository(owner:$owner,name:$repo){
    pullRequest(number:$pr){
      reviewThreads(first:100){
        nodes{ id isResolved comments(first:1){ nodes{ databaseId path } } }
      }
    }
  }
}' -F owner="$owner" -F repo="$repo" -F pr="$pr" \
  --jq '.data.repository.pullRequest.reviewThreads.nodes[] | "\(.id) \(.isResolved) \(.comments.nodes[0].databaseId) \(.comments.nodes[0].path)"'
