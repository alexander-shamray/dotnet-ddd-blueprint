#!/usr/bin/env bash
# Run Grok's /review-branch in a disposable git worktree, so the external
# reviewer never touches the session's checkout: its repository-wide edit
# grant lands in a copy that is removed afterwards, and the only artefact
# imported back is suggestions.md — by construction, not by a post-run
# git-status check that an executed-then-reverted payload would pass.
#
# Residual, stated rather than hidden: the reviewer still runs with the
# host's ambient credentials and network — gh auth included. Stripping those
# needs a container, which is an infrastructure decision this script does
# not smuggle in (docs/superpowers/specs/2026-08-08-review-loop-hardening-
# findings.md, finding 3).
set -euo pipefail
branch=$(git branch --show-current)
[ -n "$branch" ] || { echo "not on a branch" >&2; exit 2; }
# suggestions.md is the one file allowed to differ — it is the review's own
# working state. Anything else, tracked or untracked, means the reviewer
# would read a state the PR does not carry: the worktree holds only commits.
[ -z "$(git status --porcelain | grep -v '^?? suggestions.md$' || true)" ] ||
  { echo "tree has uncommitted changes; commit before the review, or the reviewer reads a state the PR does not carry" >&2; exit 3; }
wt=$(mktemp -d "${TMPDIR:-/tmp}/grok-review-XXXXXX")
cleanup() {
  git worktree remove --force "$wt" 2>/dev/null || true
  rm -rf "$wt" 2>/dev/null || true
}
trap cleanup EXIT
git worktree add --detach "$wt" "$branch" >/dev/null
# The recheck contract: an existing suggestions.md is the file the review
# re-verifies, so it crosses into the copy; nothing else does.
[ -f suggestions.md ] && cp suggestions.md "$wt/suggestions.md"
grok -p "/review-branch" --permission-mode acceptEdits --cwd "$wt"
# Import the one artefact the review owns. Its absence in the worktree is
# the clean verdict, and the real tree mirrors it either way.
if [ -f "$wt/suggestions.md" ]; then
  cp "$wt/suggestions.md" suggestions.md
else
  rm -f suggestions.md
fi
