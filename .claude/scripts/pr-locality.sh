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
# The output is the two rows, one per line, or nothing when the body carries
# neither. A caller that reads nothing skips its touch-set check and says so;
# it does not infer a class.
#
# **A pull request author is not a trusted party**, and this helper is the
# one place their text would reach an agent unfiltered: /review-copilot takes
# any PR number, and the feed helpers beside this one filter by author before
# a word of a review is shown. So each row is held to a grammar before it is
# printed — a class cell is letters A–E joined by `+`, a touch-set cell is a
# comma-separated list of path tokens in backticks or bare, and nothing else
# — and a row that fails it is refused with exit 3, naming the row and not
# its content. Prose after the cell is what an injection would be, and it
# never leaves this script.
set -euo pipefail
pr="${1:?usage: pr-locality.sh <pr-number>}"
[[ "$pr" =~ ^[0-9]+$ ]] || { echo "pr must be a number" >&2; exit 2; }
# The body is captured before it is filtered, so a `gh` failure — no
# authentication, no network, no such pull request — is fatal under `set -e`
# rather than indistinguishable from a body with no rows. Only grep's own
# no-match status, which is exactly 1, is masked; any other status is a
# fault and propagates.
body=$(gh pr view "$pr" --json body --jq .body)
class_row=$(grep -E '^\| *Class *\|' <<<"$body" || [ $? -eq 1 ])
touch_row=$(grep -E '^\| *Touch set *\|' <<<"$body" || [ $? -eq 1 ])
# Exactly one of each, or none. A second row is where a valid first row
# would have carried an invalid second past a check that only asked whether
# any row matched — `grep -q` answers for the set, and the print below emits
# the set — so two rows is refused before either grammar is consulted.
if [ "$(grep -c . <<<"$class_row")" -gt 1 ]; then
  echo "more than one Class row" >&2; exit 3
fi
if [ "$(grep -c . <<<"$touch_row")" -gt 1 ]; then
  echo "more than one Touch set row" >&2; exit 3
fi
if [ -n "$class_row" ]; then
  grep -Eq '^\| *Class *\| *[A-E](\+[A-E])* *\| *$' <<<"$class_row" ||
    { echo "the Class row is not a class" >&2; exit 3; }
fi
if [ -n "$touch_row" ]; then
  token='`?[A-Za-z0-9_./*{},()-]+`?'
  grep -Eq "^\| *Touch set *\| *${token}( *, *${token})* *\| *\$" <<<"$touch_row" ||
    { echo "the Touch set row is not a path list" >&2; exit 3; }
fi
[ -n "$class_row" ] && printf '%s\n' "$class_row"
[ -n "$touch_row" ] && printf '%s\n' "$touch_row"
exit 0
