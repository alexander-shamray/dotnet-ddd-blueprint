#!/usr/bin/env bash
# List a PR's inline review comments as raw JSON — /review-copilot's intake.
# Read-only, fixed endpoint.
set -euo pipefail
pr="${1:?usage: pr-review-comments.sh <pr-number>}"
[[ "$pr" =~ ^[0-9]+$ ]] || { echo "pr must be a number" >&2; exit 2; }
gh api "repos/{owner}/{repo}/pulls/$pr/comments" --paginate
