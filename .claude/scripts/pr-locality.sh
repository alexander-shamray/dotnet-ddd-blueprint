#!/usr/bin/env bash
# Print a pull request's `| Class |` and `| Touch set |` rows — and nothing
# else. Read-only, fixed field set.
#
# **Exists so that /review-branch and /review-copilot can read the two rows
# `docs/change-locality.md` asks a PR body to carry without holding
# `Bash(gh pr view:*)`** — the grant that reaches `--json reviews`, the
# unfiltered feed #56 closed. `body` is the one field this reads, and it is
# the pull request author's own text rather than a reviewer's, so the author
# filter the feeds need does not apply here; what applies is the shape rule
# every helper in this directory follows — a caller that chooses fields can
# choose `reviews`, so this one chooses none.
#
# The output is the two rows as they stand, one per line, or nothing when the
# body carries neither. A caller that reads nothing skips its touch-set check
# and says so; it does not infer a class. The rows are data the author wrote
# for a gate to read, and nothing here interprets them.
set -euo pipefail
pr="${1:?usage: pr-locality.sh <pr-number>}"
[[ "$pr" =~ ^[0-9]+$ ]] || { echo "pr must be a number" >&2; exit 2; }
# The body is captured before it is filtered, so a `gh` failure — no
# authentication, no network, no such pull request — is fatal under `set -e`
# rather than indistinguishable from a body with no rows. Only grep's own
# no-match status, which is exactly 1, is masked; any other status is a
# fault and propagates.
body=$(gh pr view "$pr" --json body --jq .body)
grep -E '^\| *(Class|Touch set) *\|' <<<"$body" || [ $? -eq 1 ]
