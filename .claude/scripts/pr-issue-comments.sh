#!/usr/bin/env bash
# List a PR's issue comments as JSON — /review-copilot's step-3 intake.
#
# **New with #56.** The third of the three feeds, and the one whose Copilot
# login has never been observed: six PRs were checked at filing time — #112,
# #101, #100, #99, #98, #94 — and not one carried a Copilot-authored issue
# comment. So the admitted spelling here is what `gh pr view`'s shared GraphQL
# exporter MUST report if Copilot ever posts to this feed, and nothing has seen
# it do so. That is written down in review-copilot.md's feed table as an
# inference rather than a measurement, and it is repeated here rather than
# quietly relied on.
#
# The filter is worth having on exactly that account. This feed carries no
# observed Copilot traffic and is wide open to every other account, so an
# unfiltered read of it is a pure intake of strangers' text into a command that
# holds `Edit`. It is the feed with the worst ratio of the three.
#
# stdout is the admitted subset as a JSON array — the `comments` array
# unwrapped, matching the other two helpers. Dropped count and locations to
# stderr; bodies nowhere.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/copilot-authors.sh"
pr="${1:?usage: pr-issue-comments.sh <pr-number>}"
[[ "$pr" =~ ^[0-9]+$ ]] || { echo "pr must be a number" >&2; exit 2; }
# Resolved before the feed is fetched: with `set -e` a failure here stops the
# run, where the same call inline would reach jq as an empty --argjson and
# report a parse error instead of the missing owner.
admitted=$(copilot_admitted_json)
gh pr view "$pr" --json comments |
  jq '.comments // []' |
  copilot_partition "$admitted" '.author.login' '.url' 'issue comments'
