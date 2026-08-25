#!/usr/bin/env bash
# Feed the closure gate — a PR's number, url, body, commits, GitHub's own
# closing-issue parse and the head oid, as JSON on stdout. Read-only, fixed
# field set.
#
# **Exists so that pr.md need not hold `Bash(gh pr view:*)` (#56).** This was
# the third command carrying that grant, after review-copilot.md and ship.md,
# and it was found by the test whose subject is every command's frontmatter
# rather than by reading the two files the issue named — which is the whole
# argument for writing the gate's test against the surface instead of against
# the instance.
#
# The field set is exactly what closure_gate.py reads, and it is fixed here for
# the reason every helper in this directory fixes its endpoint: a caller that
# chooses its own fields can choose `reviews`, which is the unfiltered route
# the three feed helpers exist to close.
#
# `body` and `commits` cross here and that is intentional — both are the
# repository's own text, and the consumer is a parser rather than a model.
set -euo pipefail
pr="${1:?usage: pr-closure-input.sh <pr-number>}"
[[ "$pr" =~ ^[0-9]+$ ]] || { echo "pr must be a number" >&2; exit 2; }
gh pr view "$pr" --json number,url,body,commits,closingIssuesReferences,headRefOid
