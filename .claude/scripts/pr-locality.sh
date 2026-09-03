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
# it does not infer a class. One row without the other is refused, because
# each command reads the pair — a class with no set has no bound, a set with
# no class has no map.
#
# **A pull request author is not a trusted party**, and this helper is the
# one place their text would reach an agent unfiltered: /review-copilot takes
# any PR number, and the feed helpers beside this one filter by author before
# a word of a review is shown. So each row is held to a grammar before it is
# printed, and a row that fails it is refused with exit 3, naming the row and
# not its content. A class cell is one letter A–E, or two distinct letters
# joined by `+`. A touch-set cell is a comma-separated list of path tokens,
# each bare or in balanced backticks, made of path characters and glob
# characters only, and every token is repository-relative: no leading `/`,
# no leading `./`, no `..` segment — the edit-target guard judges where an
# edit inside the checkout lands and not a path that names the outside, so
# the outside is refused here. Prose after a cell is what an injection would
# be, and it never leaves this script.
set -euo pipefail
pr="${1:?usage: pr-locality.sh <pr-number>}"
[[ "$pr" =~ ^[0-9]+$ ]] || { echo "pr must be a number" >&2; exit 2; }
refuse() { echo "$1" >&2; exit 3; }
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
# any row matched, so two rows is refused before either grammar is consulted.
[ "$(grep -c . <<<"$class_row")" -le 1 ] || refuse "more than one Class row"
[ "$(grep -c . <<<"$touch_row")" -le 1 ] || refuse "more than one Touch set row"
if [ -z "$class_row" ] && [ -z "$touch_row" ]; then exit 0; fi
[ -n "$class_row" ] && [ -n "$touch_row" ] || refuse "one row without the other"
# The class cell: the text between the second `|` and the closing one.
class=$(sed -E 's/^\| *Class *\| *//; s/ *\| *$//' <<<"$class_row")
grep -Eq '^[A-E](\+[A-E])?$' <<<"$class" || refuse "the Class row is not a class"
[ "${class:0:1}" != "${class:2:1}" ] || refuse "the Class row repeats a class"
# The touch-set cell, then each comma-separated token on its own.
cells=$(sed -E 's/^\| *Touch set *\| *//; s/ *\| *$//' <<<"$touch_row")
case "$cells" in *'|'*) refuse "the Touch set row is not one cell" ;; esac
[ -n "$cells" ] || refuse "the Touch set row is empty"
# Split on commas outside braces, because a brace glob carries its own —
# `.claude/commands/{pr,ship}.md` is one token, not two halves of one.
items=(); cur=""; depth=0
for ((i = 0; i < ${#cells}; i++)); do
  ch="${cells:i:1}"
  case "$ch" in
    '{') depth=$((depth + 1)) ;;
    '}') depth=$((depth - 1)); [ "$depth" -ge 0 ] || refuse "the Touch set row has an unbalanced brace" ;;
    ',') if [ "$depth" -eq 0 ]; then items+=("$cur"); cur=""; continue; fi ;;
  esac
  cur+="$ch"
done
items+=("$cur")
[ "$depth" -eq 0 ] || refuse "the Touch set row has an unbalanced brace"
for item in "${items[@]}"; do
  t="${item#"${item%%[! ]*}"}"
  t="${t%"${t##*[! ]}"}"
  case "$t" in
    '`'*'`') t="${t:1:${#t}-2}" ;;
    *'`'*) refuse "the Touch set row has an unbalanced backtick" ;;
  esac
  grep -Eq '^[A-Za-z0-9_./*{},()-]+$' <<<"$t" ||
    refuse "the Touch set row is not a path list"
  case "$t" in /*|./*) refuse "the Touch set row names a path outside the repository" ;; esac
  case "/$t/" in */../*) refuse "the Touch set row names a path outside the repository" ;; esac
done
printf '%s\n' "$class_row" "$touch_row"
