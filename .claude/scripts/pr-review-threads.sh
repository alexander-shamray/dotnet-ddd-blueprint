#!/usr/bin/env bash
# Map a PR's review threads: thread id, resolved state, first comment's
# database id and path — the join /review-copilot needs, because resolution
# is a GraphQL mutation on the thread id, not the comment id. Cursor-
# paginated: step 6's clean exit requires zero unresolved threads, and a
# fixed first:100 would silently omit every thread after the first page.
# Read-only; the query text is fixed here.
set -euo pipefail
pr="${1:?usage: pr-review-threads.sh <pr-number>}"
[[ "$pr" =~ ^[0-9]+$ ]] || { echo "pr must be a number" >&2; exit 2; }
owner=$(gh repo view --json owner --jq .owner.login)
repo=$(gh repo view --json name --jq .name)
cursor=""
while :; do
  resp=$(gh api graphql -f query='
  query($owner:String!,$repo:String!,$pr:Int!,$after:String){
    repository(owner:$owner,name:$repo){
      pullRequest(number:$pr){
        reviewThreads(first:100, after:$after){
          pageInfo{ hasNextPage endCursor }
          nodes{ id isResolved comments(first:1){ nodes{ databaseId path } } }
        }
      }
    }
  }' -F owner="$owner" -F repo="$repo" -F pr="$pr" ${cursor:+-F after="$cursor"})
  jq -r '.data.repository.pullRequest.reviewThreads.nodes[] |
    "\(.id) \(.isResolved) \(.comments.nodes[0].databaseId) \(.comments.nodes[0].path)"' <<<"$resp"
  [ "$(jq -r '.data.repository.pullRequest.reviewThreads.pageInfo.hasNextPage' <<<"$resp")" = "true" ] || break
  cursor=$(jq -r '.data.repository.pullRequest.reviewThreads.pageInfo.endCursor' <<<"$resp")
done
