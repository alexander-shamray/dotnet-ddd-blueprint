#!/usr/bin/env bash
# Fork the detached, pinned worktree a sweep audits in — /security-sweep or
# /bug-sweep — and nothing else: `git worktree add --detach <path> <commit>`.
#
# A `Bash(git worktree add:*)` grant would also buy `-B`, which resets an
# existing branch — the operation .claude/settings.json denies as
# `git branch --force` and `-M`. That is the same hole /branch closed with
# git-worktree-fork.sh, and a prefix rule cannot exclude a flag: see
# git-switch-existing.sh, where the flags were shown to combine.
#
# --detach is fixed here rather than passed, because a sweep worktree carries
# no commits and must never hold a branch: the caller's branch stays checked
# out where it is, which is the whole reason that command takes a detached one.
set -euo pipefail
[ "$#" -eq 2 ] || { echo "usage: git-worktree-detach.sh <path> <commit-sha>" >&2; exit 2; }
path="$1"
commit="$2"
# The caller makes this with `mktemp -d`, so it exists and is empty. Requiring
# both is what stops a path that happens to hold something from being handed to
# git, and neither argument can begin with '-'.
case "$path" in -*) echo "path may not start with '-'" >&2; exit 2 ;; esac
[ -d "$path" ] || { echo "not an existing directory: $path" >&2; exit 2; }
[ -z "$(ls -A "$path" 2>/dev/null)" ] || { echo "directory is not empty: $path" >&2; exit 2; }
# **Registration is not ownership, and neither is emptiness.** The audited tree
# is prompt-injection input, so a path arriving here may be chosen by it — and
# the residual this helper was written to close is precisely about *which* path,
# not which flags. So the path must be one only a sweep's own `mktemp -d` could
# have produced: `secsweep-` plus six characters — a prefix that is historical
# and shared, /bug-sweep having borrowed it rather than widen this check —
# directly under the canonical temp root. An unrelated empty directory
# elsewhere on the host, or a sibling PR worktree that happens to be
# registered, is refused.
tmproot=$(cd "${TMPDIR:-/tmp}" 2>/dev/null && pwd -P) ||
  { echo "cannot resolve the temp root" >&2; exit 4; }
resolved=$(cd "$path" && pwd -P)
case "$resolved" in
  "$tmproot"/secsweep-??????) : ;;
  *) echo "not a sweep-owned temp path: $path" >&2; exit 2 ;;
esac
# A resolved sha, never a ref: the caller reads `git rev-parse HEAD` once and
# passes the result, precisely so HEAD is not resolved a second time under a
# tree another session may have moved.
[[ "$commit" =~ ^[0-9a-f]{40}$ ]] ||
  { echo "commit must be a full 40-character sha: $commit" >&2; exit 2; }
git rev-parse --verify --quiet "$commit^{commit}" >/dev/null ||
  { echo "no such commit: $commit" >&2; exit 3; }
git worktree add --detach "$resolved" "$commit"
