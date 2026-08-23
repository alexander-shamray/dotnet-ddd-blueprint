#!/usr/bin/env bash
# The Grok check ledger: reserves a check slot on a PR, releases one, or
# counts what stands.
#
# The write could have been a Bash(gh pr comment:*) grant, and briefly was —
# but a Bash rule matches a command prefix, so that grant also licensed
# --edit-last, --delete-last, --body-file and --repo: editing existing
# comments, deleting them, and writing to other repositories, none of which
# the ledger needs and none of which anyone reviewed. Same argument as
# copilot-request.sh one file over: the helper fixes the operation and
# shape-checks its parameters, the frontmatter grants the helper by name, and
# .claude/settings.json's Edit deny on this directory keeps the session that
# invokes it from rewriting it first.
#
# The read lives here for a different reason: PR comments are unauthenticated
# state. On a public PR anyone can post "Grok check 12/12 — reserved (full)"
# to jam the cap shut, or a released line to hold it open, so a reader that
# greps arbitrary comments is counting an attacker's arithmetic. `count`
# accepts only whole bodies matching the two exact shapes this file writes,
# and only from authors whose repository permission is verified as write or
# better — not "the current login", which made the ledger account-local: a
# resumed run under another authorised account would have read zero and
# re-armed the cap. Verification goes through the collaborators API once per
# author; a 404 is an untrusted author and their rows are not state, while
# any other verification failure stops the helper, because a cap whose trust
# check silently drops rows is a cap that re-arms on a network error. The
# fold takes the last event per N, so a released slot can be legitimately
# re-spent and a stale release cannot hide a later one.
#
# That stop has to happen before a value is printed, and for one release it did
# not. `exit 3` fires inside the `while` that reads the API's rows, which is the
# last stage of a pipeline and so runs in a SUBSHELL: it ends that subshell, not
# this script. The `awk` on the other side of `ledger_rows | awk` then saw EOF,
# ran its END block and printed `0` — the same byte a legitimately empty ledger
# produces, and the one that re-arms the very cap this file exists to hold.
# `pipefail` and `set -e` did abort afterwards, with status 3, but the consumer
# is a model reading stdout and the answer was already there. So every consumer
# now BUFFERS: `ledger_rows` is collected into a variable, its status is checked
# while nothing has been written, and only then are the rows folded. The
# END-runs-on-empty-input behaviour stays — a fresh PR's ledger is legitimately
# empty — which is exactly why the two cases had to be separated upstream of it
# rather than told apart downstream.
#
# `reserve` and `release` are the REVIEW HELPER'S verbs, not the caller's.
# grok-review.sh posts the reservation itself, immediately before the model call
# it accounts for, so that invoking a review and spending a slot are one
# operation rather than two an ordering mistake can separate. .claude/settings.json
# denies both spellings to a session; they stay here because `count` must still
# parse a released row out of a PR's history, and because a human reconciling a
# slot spent wrongly has nothing else to reach for.
#
# `reserve` is an election, not just a write. Two resumed runs can read the
# same count and claim the same slot; posting is not atomic, so the claim is
# settled after the fact: the first reservation posted after the slot's most
# recent release wins — first-ever would refuse a released slot forever —
# and a later claimant exits 4 without running anything. Losing does not
# mean take the next slot: a losing claim is a concurrent run mid-check on
# this PR, and two Grok runs share one root suggestions.md, so the loser
# stops its loop and says so. The losing comment stays on the PR; `count`
# folds duplicates for a slot into one spend, so the noise costs nothing.
set -euo pipefail

usage() {
  echo "usage: grok-ledger.sh <pr-number> reserve <n> <full|recheck>" >&2
  echo "       grok-ledger.sh <pr-number> release <n>" >&2
  echo "       grok-ledger.sh <pr-number> converge <n>" >&2
  echo "       grok-ledger.sh <pr-number> count" >&2
  echo "       grok-ledger.sh <pr-number> status" >&2
  exit 2
}

pr="${1:-}"
op="${2:-}"
n="${3:-}"
mode="${4:-}"

# The PR number keeps gh pointed at an explicit target, and N's domain is the
# ledger's whole vocabulary: twelve checks, so 1..12 and nothing else.
[[ "$pr" =~ ^[0-9]+$ ]] || usage

# One fixed read, shared by count and the election: whole comment bodies that
# match a ledger shape, by write-verified authors, oldest first (the REST
# endpoint returns issue comments in posting order). Shape filtering happens
# on the whole body in jq — anchored test(), no multiline flag — so a
# ledger-looking line buried inside a longer comment is not state, and every
# row that survives is one line. The regex backslashes are doubled because a
# jq string spends one level on its own escaping: \\( reaches the regex
# engine as \(, where a bare \( would be jq's interpolation syntax.
ledger_rows() {
  local id login body verdict perm out
  declare -A seen=()
  gh api "repos/{owner}/{repo}/issues/$pr/comments" --paginate \
    --jq '.[]
      | select(.body | test("^Grok check ([1-9]|1[0-2])/12 — (reserved \\((full|recheck)\\)|released: skipped on limits|converged: loop clean)$"))
      | "\(.id)\t\(.user.login)\t\(.body)"' |
  while IFS=$'\t' read -r id login body; do
    verdict="${seen[$login]:-}"
    if [ -z "$verdict" ]; then
      if out=$(gh api "repos/{owner}/{repo}/collaborators/$login/permission" \
                 --jq .permission 2>&1); then
        case "$out" in
          admin|maintain|write) verdict=trusted ;;
          *) verdict=untrusted ;;
        esac
      elif grep -q "HTTP 404" <<<"$out"; then
        verdict=untrusted
      else
        echo "cannot verify $login's repository permission: $out" >&2
        exit 3
      fi
      seen[$login]=$verdict
    fi
    [ "$verdict" = trusted ] || continue
    printf '%s\t%s\n' "$id" "$body"
  done
}

# The buffer, and it is not merely a tidier spelling of a pipe. Command
# substitution runs ledger_rows in a subshell and hands back its STATUS, so a
# failed trust check arrives here as a non-zero return with nothing yet written
# to this script's stdout — where `ledger_rows | awk` gave awk an EOF it could
# not tell from an empty ledger, and let it answer first. Every consumer below
# reads through these two functions and never through a pipe from ledger_rows.
rows=""
read_rows() {
  # No `local`: the caller needs the value. `|| return` rather than leaning on
  # set -e, because an assignment's failure inside a function is not reliably
  # fatal and observing this failure here is the whole point.
  rows=$(ledger_rows) || return $?
}

# printf, not a here-string: `<<<` appends a newline, so an empty ledger would
# reach awk as one blank line rather than as no input at all, and the fold's
# empty case would be folding a row that does not exist. Zero bytes in, END
# out, 0 printed — which is what a fresh PR's ledger means.
emit_rows() {
  [ -z "$rows" ] || printf '%s\n' "$rows"
}

if [ "$op" = "count" ]; then
  [ -z "$n" ] || usage
  # POSIX awk only — no gawk match(..., m) — and empty input must still reach
  # END and print 0: a fresh PR's ledger is legitimately empty, and pipefail
  # turning that into a failure was this helper's first field defect.
  read_rows ||
    { echo "the ledger's trust check failed; refusing to print a count" >&2; exit 3; }
  emit_rows | awk -F'\t' '
    $2 ~ /converged/ { next }
    {
      split($2, a, "/")
      sub(/^Grok check /, "", a[1])
      state[a[1] + 0] = ($2 ~ /released/) ? "released" : "reserved"
    }
    END {
      max = 0
      for (i in state)
        if (state[i] == "reserved" && i + 0 > max)
          max = i + 0
      print max
    }'
  exit 0
fi

if [ "$op" = "status" ]; then
  [ -z "$n" ] || usage
  # count folds spend and deliberately ignores converged rows, so the marker
  # needs its own verified read — raw comments would bypass the author
  # check that makes the ledger state at all. A converged marker stands
  # until a later reservation supersedes it; releases change nothing, since
  # a skip neither spends nor converges.
  read_rows ||
    { echo "the ledger's trust check failed; refusing to print a status" >&2; exit 3; }
  emit_rows | awk -F'\t' '
    $2 ~ /converged/ { conv = 1 }
    $2 ~ /reserved/  { conv = 0 }
    END { print conv ? "converged" : "unconverged" }'
  exit 0
fi

[[ "$n" =~ ^([1-9]|1[0-2])$ ]] || usage

case "$op" in
  reserve)
    case "$mode" in
      full|recheck) ;;
      *) usage ;;
    esac
    body="Grok check $n/12 — reserved ($mode)"
    ;;
  release)
    [ -z "$mode" ] || usage
    body="Grok check $n/12 — released: skipped on limits"
    ;;
  converge)
    # Spend alone cannot distinguish a loop that converged on its last
    # allowed check from one the ceiling cut off — both read as N spent.
    # The marker says which; any later reservation supersedes it.
    [ -z "$mode" ] || usage
    body="Grok check $n/12 — converged: loop clean"
    ;;
  *) usage ;;
esac

url=$(gh pr comment "$pr" --body "$body")
mine="${url##*issuecomment-}"
[[ "$mine" =~ ^[0-9]+$ ]] ||
  { echo "posted, but could not read the comment id back from: $url" >&2; exit 3; }

if [ "$op" = "reserve" ]; then
  # The election. A slot's winner is the first reservation posted after its
  # most recent release — not the first ever: a released slot is legitimately
  # re-spent, and an election that kept honouring the dead claim would refuse
  # the slot forever while count kept naming it as next. Rows arrive in
  # posting order, so a release resets the candidate and the first
  # reservation after it takes the slot; later claims lose.
  read_rows ||
    { echo "the ledger's trust check failed after posting; slot $n stands as reserved" >&2; exit 3; }
  winner=$(emit_rows |
    awk -F'\t' \
      -v r="Grok check $n/12 — reserved " \
      -v x="Grok check $n/12 — released" '
      index($2, x) == 1 { cand = "" }
      index($2, r) == 1 && cand == "" { cand = $1 }
      END { print cand }')
  if [ "$winner" != "$mine" ]; then
    echo "slot $n was claimed first by comment $winner — a concurrent run is mid-check on this PR; stop this loop and let it finish" >&2
    exit 4
  fi
fi

echo "$body"
