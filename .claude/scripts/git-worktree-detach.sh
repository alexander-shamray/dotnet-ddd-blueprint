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
# not which flags. So the path must match `secsweep-` plus six characters under
# the canonical temp root — a prefix that is historical and shared, /bug-sweep
# having borrowed it rather than widen this check.
#
# Be precise about what that buys, because two stronger readings are wrong and
# both stood in this comment before either was tested.
#
# It EXCLUDES an unrelated empty directory elsewhere on the host, and every
# sibling PR worktree that happens to be registered. That is the point: a
# poisoned finding naming a sibling must not be able to delete someone's
# workspace.
#
# It does NOT prove a sweep made this path. `Bash(mktemp:*)` takes an arbitrary
# template, and git-worktree-drop.sh accepts any registered worktree of the
# right shape, so "only a sweep's own mktemp could have produced it" is not a
# property this check has.
#
# It is NOT strictly a direct-child check either, though this comment claimed
# so. A bash `case` does no pathname expansion, so `?` matches `/` like any
# other character and $tmproot/secsweep-a/bbbb passes as readily as
# $tmproot/secsweep-abc123 — run through a `case` rather than reasoned about,
# with wrong-length, wrong-prefix and wrong-root controls all correctly refused.
# Prefix and length hold; direct-childness does not.
#
# Fix owed, one line: compare `dirname "$resolved"` against "$tmproot" and match
# the basename alone. The same line is owed in git-worktree-drop.sh.
tmproot=$(cd "${TMPDIR:-/tmp}" 2>/dev/null && pwd -P) ||
  { echo "cannot resolve the temp root" >&2; exit 4; }
resolved=$(cd "$path" && pwd -P)
case "$resolved" in
  "$tmproot"/secsweep-??????) : ;;
  *) echo "not a sweep-shaped temp path: $path" >&2; exit 2 ;;
esac
# A resolved sha, never a ref: the caller reads `git rev-parse HEAD` once and
# passes the result, precisely so HEAD is not resolved a second time under a
# tree another session may have moved.
[[ "$commit" =~ ^[0-9a-f]{40}$ ]] ||
  { echo "commit must be a full 40-character sha: $commit" >&2; exit 2; }
git rev-parse --verify --quiet "$commit^{commit}" >/dev/null ||
  { echo "no such commit: $commit" >&2; exit 3; }
git worktree add --detach "$resolved" "$commit"
