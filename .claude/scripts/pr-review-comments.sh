#!/usr/bin/env bash
# List a PR's inline review comments as raw JSON — /review-copilot's intake.
# Read-only, fixed endpoint.
#
# **Filtered by author since #56.** This feed is open to any GitHub account on
# a public PR, and /review-copilot reaches it holding `Edit` while /ship runs
# that command unattended in a loop. The filter is here rather than in the
# command's prose because prose is what #56 was filed about: a triage that
# skipped the author rule was indistinguishable from one that ran it.
#
# stdout is the admitted subset, same JSON array shape as the unfiltered feed,
# so a caller that parsed the old output parses this. The dropped count and the
# dropped authors' LOCATIONS go to stderr; their bodies go nowhere.
#
# Pages are slurped before filtering. With --paginate, gh emits one array per
# page, and a per-page filter would hand the caller several arrays where it
# expects one — copilot-request-count.sh documents the same hazard.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/copilot-authors.sh"
pr="${1:?usage: pr-review-comments.sh <pr-number>}"
[[ "$pr" =~ ^[0-9]+$ ]] || { echo "pr must be a number" >&2; exit 2; }
# Resolved before the feed is fetched: with `set -e` a failure here stops the
# run, where the same call inline would reach jq as an empty --argjson and
# report a parse error instead of the missing owner.
admitted=$(copilot_admitted_json)
gh api "repos/{owner}/{repo}/pulls/$pr/comments" --paginate |
  jq -s 'add // []' |
  copilot_partition "$admitted" '.user.login' '.html_url' 'inline comments'
