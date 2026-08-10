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
# only from the account gh is authenticated as — writer and reader are the
# same login by construction — and takes the last event per N, so a released
# slot can be legitimately re-spent and a stale release cannot hide a later
# one.
#
# `reserve` is an election, not just a write. Two resumed runs can read the
# same count and claim the same slot; posting is not atomic, so the claim is
# settled after the fact: the first reservation posted after the slot's most
# recent release wins — first-ever would refuse a released slot forever —
# and a later claimant exits 4 without running anything, re-reads the count
# and reserves the next slot. The losing comment stays on the PR; `count`
# folds duplicates for a slot into one spend, so the noise costs nothing.
set -euo pipefail

usage() {
  echo "usage: grok-ledger.sh <pr-number> reserve <n> <full|recheck>" >&2
  echo "       grok-ledger.sh <pr-number> release <n>" >&2
  echo "       grok-ledger.sh <pr-number> count" >&2
  exit 2
}

pr="${1:-}"
op="${2:-}"
n="${3:-}"
mode="${4:-}"

# The PR number keeps gh pointed at an explicit target, and N's domain is the
# ledger's whole vocabulary: twelve checks, so 1..12 and nothing else.
[[ "$pr" =~ ^[0-9]+$ ]] || usage

# One fixed read, shared by count and the election: whole comment bodies by
# the authenticated login that match a ledger shape, oldest first (the REST
# endpoint returns issue comments in posting order). Shape filtering happens
# on the whole body in jq — anchored test(), no multiline flag — so a
# ledger-looking line buried inside a longer comment is not state, and every
# row that survives is one line. The regex backslashes are doubled because a
# jq string spends one level on its own escaping: \\( reaches the regex
# engine as \(, where a bare \( would be jq's interpolation syntax.
ledger_rows() {
  local me
  me=$(gh api user --jq .login)
  [ -n "$me" ] || { echo "cannot resolve the authenticated gh login" >&2; exit 3; }
  gh api "repos/{owner}/{repo}/issues/$pr/comments" --paginate \
    --jq '.[] | select(.user.login == "'"$me"'")
      | select(.body | test("^Grok check ([1-9]|1[0-2])/12 — (reserved \\((full|recheck)\\)|released: skipped on limits)$"))
      | "\(.id)\t\(.body)"'
}

if [ "$op" = "count" ]; then
  [ -z "$n" ] || usage
  # POSIX awk only — no gawk match(..., m) — and empty input must still reach
  # END and print 0: a fresh PR's ledger is legitimately empty, and pipefail
  # turning that into a failure was this helper's first field defect.
  ledger_rows | awk -F'\t' '
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
  winner=$(ledger_rows |
    awk -F'\t' \
      -v r="Grok check $n/12 — reserved " \
      -v x="Grok check $n/12 — released" '
      index($2, x) == 1 { cand = "" }
      index($2, r) == 1 && cand == "" { cand = $1 }
      END { print cand }')
  if [ "$winner" != "$mine" ]; then
    echo "slot $n was claimed first by comment $winner — this claim lost; re-read the count and reserve the next slot" >&2
    exit 4
  fi
fi

echo "$body"
