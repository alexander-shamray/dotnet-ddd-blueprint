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
#
# **The PATH is made here rather than passed, and that is what turned the shape
# check from a guess into a fact.** The caller used to run its own `mktemp -d`
# and hand the result over, which meant two things at once: the sweeps needed a
# `Bash(mktemp:*)` grant, and `mktemp` takes an arbitrary template — so the
# grant was a filesystem-write primitive able to create an empty directory or
# file anywhere the session can write, the checkout included. It could not write
# content and could not clobber, so no source file was ever reachable through
# it; but "the only mutations are the issues it files and the worktree" was
# false, and a prefix rule cannot constrain a template. Now the only path git is
# ever handed is one this script has just created, and both sweeps drop the
# grant altogether.
set -euo pipefail
[ "$#" -eq 1 ] || { echo "usage: git-worktree-detach.sh <commit-sha>" >&2; exit 2; }
commit="$1"
# A resolved sha, never a ref: the caller reads `git rev-parse HEAD` once and
# passes the result, precisely so HEAD is not resolved a second time under a
# tree another session may have moved. Checked before anything is created, so a
# bad argument leaves no directory behind.
[[ "$commit" =~ ^[0-9a-f]{40}$ ]] ||
  { echo "commit must be a full 40-character sha: $commit" >&2; exit 2; }
git rev-parse --verify --quiet "$commit^{commit}" >/dev/null ||
  { echo "no such commit: $commit" >&2; exit 3; }

tmproot=$(cd "${TMPDIR:-/tmp}" 2>/dev/null && pwd -P) ||
  { echo "cannot resolve the temp root" >&2; exit 4; }
# Six X's, so the name `mktemp` invents is `secsweep-` plus exactly six
# characters — the shape git-worktree-drop.sh will later require before it
# removes anything. The two ends of the sweep's lifetime agree because one of
# them produced the name.
path=$(mktemp -d "$tmproot/secsweep-XXXXXX") ||
  { echo "cannot create a sweep worktree directory under $tmproot" >&2; exit 4; }
resolved=$(cd "$path" && pwd -P)

# The shape check, kept even though this script made the path, because it is the
# contract git-worktree-drop.sh depends on and a check that only holds while
# nobody edits the line above is not a contract. It is cheap and it fails loudly.
#
# **It is a direct-child check now, and it was not before.** The old form was a
# single `case "$resolved" in "$tmproot"/secsweep-??????)`, and a bash `case`
# does no pathname expansion — so `?` matches `/` like any other character and
# `$tmproot/secsweep-a/bbbb` passed as readily as `$tmproot/secsweep-abc123`.
# Run through a `case` rather than reasoned about, with wrong-length,
# wrong-prefix and wrong-root controls all correctly refused: prefix and length
# held, direct-childness did not. Splitting the two halves is what fixes it —
# a basename contains no `/`, so `??????` can only mean six real characters.
[ "$(dirname "$resolved")" = "$tmproot" ] ||
  { echo "not a direct child of the temp root: $resolved" >&2; exit 2; }
case "$(basename "$resolved")" in
  secsweep-??????) : ;;
  *) echo "not a sweep-shaped temp path: $resolved" >&2; exit 2 ;;
esac

# Redirected, and the redirection is load-bearing rather than tidy. This script's
# stdout IS its return value now, and `git worktree add` writes "Preparing
# worktree" to stderr but "HEAD is now at <sha> <subject>" to STDOUT — so the
# first caller to capture the output got the commit subject and the path, and
# handed both to the next command as a directory name. Found by running the
# round trip rather than by reading it: the failure is a `not an existing
# directory` naming a whole commit message, at the teardown, after the sweep.
git worktree add --detach "$resolved" "$commit" >&2
# The path, and it is the POSIX spelling — the one the shell and this helper
# share. The caller still reads `git worktree list --porcelain` for the
# host-native spelling its readers need; under MSYS those are two strings for
# one directory, and on some hosts two directories.
printf '%s\n' "$resolved"
