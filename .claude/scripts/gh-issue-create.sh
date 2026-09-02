#!/usr/bin/env bash
# File one issue on THIS repository from a sweep, and nothing else: the title
# comes from the first argument, the kind and severity labels from a fixed
# vocabulary, the body from stdin, and the repository from the checkout this
# script is standing in.
#
# `Bash(gh issue create:*)` was a prefix grant, so it bought more than the
# operation it was added for — the shape every helper in this directory exists
# to close (#75 item 5). Two free parameters reached it from an audited tree
# that is prompt-injection input:
#
#   -R/--repo  unpinned, so the issue lands wherever the argument names. Both
#              sweeps held "always `--repo` for this repository" as prose,
#              which a finding can talk past.
#   --label    unpinned, so any label in any spelling, and `gh` creates none
#              of them — a misspelt label fails the filing after the body has
#              been composed.
#
# Here the repository is what `gh repo view` resolves, the labels are two words
# out of a closed set, and the body is bytes on stdin — the form both sweeps
# already used, because an inline `--body` mangles the wrapping and a temp file
# would need the `Write` grant the sweeps withhold.
#
# THE TITLE HAZARD CLOSES HERE TOO, and this is the one thing a helper can do
# that a grant could not. MSYS argument conversion rewrites an argument that
# looks like an absolute POSIX path before a native `gh.exe` sees it, so a
# title beginning with `/` filed four times as `C:/Program Files/Git/...`
# (#55, #56, #68). The commands could not set MSYS2_ARG_CONV_EXCL, because an
# env-prefixed command no longer begins with `gh issue create` and the grant is
# a prefix match. A script sets it for its own child and the grant on the
# script is unchanged.
set -euo pipefail

[ "$#" -eq 3 ] ||
  { echo "usage: gh-issue-create.sh <title> <security|bug> <critical|high|medium|low> < body" >&2; exit 2; }
title="$1"; kind="$2"; severity="$3"

# The whole vocabulary a sweep files under. A word outside it is refused rather
# than passed on — that refusal is the point of the file — and `documentation`
# is deliberately absent: neither sweep files one.
case "$kind" in
  security|bug) ;;
  *) echo "not a kind this helper will file under: $kind" >&2; exit 2 ;;
esac
case "$severity" in
  critical|high|medium|low) ;;
  *) echo "not a severity this helper will file under: $severity" >&2; exit 2 ;;
esac

# An empty title files an issue nobody can find in the tracker, and a title with
# a newline in it is two arguments to something downstream. Both are refused.
[ -n "$title" ] || { echo "the title is empty" >&2; exit 2; }
case "$title" in
  *$'\n'*) echo "the title contains a newline" >&2; exit 2 ;;
esac

# The repository is resolved, never accepted. `gh repo view` reads the checkout
# this process is standing in, so the answer is a property of the filesystem
# rather than of anything a finding could have said.
repo=$(gh repo view --json nameWithOwner --jq .nameWithOwner) ||
  { echo "cannot resolve this checkout's repository" >&2; exit 3; }

# Both labels are ensured through the sibling helper, so a fresh clone gets the
# colour and description this repository already uses rather than a bare name.
here=$(dirname "$0")
bash "$here/gh-label-ensure.sh" "$kind" >/dev/null
bash "$here/gh-label-ensure.sh" "$severity" >/dev/null

# The body is stdin and nothing else: `--body-file -` reads bytes, which is why
# a title is the only argument the conversion below could ever have touched.
MSYS2_ARG_CONV_EXCL='*' gh issue create \
  --repo "$repo" \
  --title "$title" \
  --label "$kind" \
  --label "$severity" \
  --body-file -
