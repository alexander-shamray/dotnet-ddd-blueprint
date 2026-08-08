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
# git status is captured first so a git failure aborts under set -e rather
# than collapsing to an empty string that reads as a clean tree; only grep's
# expected no-match exit is suppressed.
status=$(git status --porcelain)
[ -z "$(grep -v '^?? suggestions.md$' <<<"$status" || true)" ] ||
  { echo "tree has uncommitted changes; commit before the review, or the reviewer reads a state the PR does not carry" >&2; exit 3; }
wt=$(mktemp -d "${TMPDIR:-/tmp}/grok-review-XXXXXX")
result=$(mktemp "${TMPDIR:-/tmp}/grok-review-result-XXXXXX")
cleanup() {
  git worktree remove --force "$wt" 2>/dev/null || true
  rm -rf "$wt" 2>/dev/null || true
  rm -f "$result" 2>/dev/null || true
}
trap cleanup EXIT
git worktree add --detach "$wt" "$branch" >/dev/null
# The recheck contract: an existing suggestions.md is the file the review
# re-verifies, so it crosses into the copy; nothing else does.
[ -f suggestions.md ] && cp suggestions.md "$wt/suggestions.md"
# bypassPermissions, not acceptEdits, and the difference is the whole review.
# acceptEdits auto-approves edits and NOT run_terminal_command, so the review
# died on the first `git` call it made to establish the branch range: the
# session was cancelled after two turns with
# cancellationCategory="PermissionCancelled", wrote no suggestions.md, and
# exited 0 — which the import below then read as a clean review. The
# disposable worktree is what makes the broader grant safe, and it already
# was: nothing about the blast radius changes here, only which tools the
# reviewer is allowed to reach for inside it.
set +e
grok -p "/review-branch" --permission-mode bypassPermissions --cwd "$wt" \
  --output-format json >"$result"
grok_status=$?
set -e
# A review that did not run must never be mirrored into a clean verdict.
# An absent suggestions.md means both "nothing to report" and "the reviewer
# never looked", and only these checks separate them — the same fail-open
# shape §13.5 names for an empty readiness predicate set, and the reason the
# cancellation above went unnoticed through a whole PR.
[ "$grok_status" -eq 0 ] ||
  { echo "grok exited $grok_status; the review did not run" >&2; exit 4; }
[ -s "$result" ] ||
  { echo "grok produced no output; the review did not run" >&2; exit 5; }
if grep -qE '"stopReason"[[:space:]]*:[[:space:]]*"(cancelled|refusal|error[^"]*)"' "$result"; then
  grep -oE '"(stopReason|cancellationCategory)"[[:space:]]*:[[:space:]]*"[^"]*"' "$result" >&2 || true
  echo "grok stopped early; the review did not run and suggestions.md is left as it was" >&2
  exit 6
fi
cat "$result"
# Import the one artefact the review owns. Its absence in the worktree is the
# clean verdict — trustworthy only because the checks above have ruled out the
# run having been cancelled before it looked at anything.
if [ -f "$wt/suggestions.md" ]; then
  cp "$wt/suggestions.md" suggestions.md
else
  rm -f suggestions.md
fi
