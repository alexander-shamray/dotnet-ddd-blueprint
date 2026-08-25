#!/usr/bin/env bash
# List a PR's review BODIES as JSON — /review-copilot's step-1 intake, and the
# feed that matters most: the `<details><summary>Suppressed comments</summary>`
# block arrives here, and ship.md records it as where every real finding
# against this machinery has actually come from.
#
# **New with #56, and it is the half that made the fix worth doing.** An
# earlier revision of review-copilot.md's residual named only the inline feed;
# filtering that one and leaving this one raw is a control that reads as
# complete while the important feed stays open. This helper exists so that
# `gh pr view --json reviews` need not be granted to a command holding `Edit`.
#
# stdout is the admitted subset as a JSON array — the `reviews` array unwrapped,
# not the `{"reviews": [...]}` envelope, so all three feed helpers hand back the
# same shape. The dropped count and locations go to stderr; bodies go nowhere.
#
# `gh pr view` loads `reviews` and `comments` through one GraphQL exporter, so
# the login here is the bare `copilot-pull-request-reviewer` — NOT the `[bot]`
# suffix, which is REST's spelling from /pulls/{n}/reviews and reaches no helper
# in this directory. copilot-authors.sh admits all three regardless.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/copilot-authors.sh"
pr="${1:?usage: pr-review-bodies.sh <pr-number>}"
[[ "$pr" =~ ^[0-9]+$ ]] || { echo "pr must be a number" >&2; exit 2; }
# Resolved before the feed is fetched: with `set -e` a failure here stops the
# run, where the same call inline would reach jq as an empty --argjson and
# report a parse error instead of the missing owner.
admitted=$(copilot_admitted_json)
gh pr view "$pr" --json reviews |
  jq '.reviews // []' |
  copilot_partition "$admitted" '.author.login' '.submittedAt' 'review bodies'
