#!/usr/bin/env bash
# The Grok check ledger: writes one line of it to a PR, or counts what stands.
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
# therefore accepts only the two exact shapes this file writes, only from the
# account gh is authenticated as — the writer and the reader are the same
# login by construction — and takes the last event per N, so a released
# reservation can be legitimately re-spent and a stale release cannot hide a
# later one.
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

if [ "$op" = "count" ]; then
  [ -z "$n" ] || usage
  # Fixed endpoint, paginated so a busy PR cannot truncate the ledger, and
  # filtered to the authenticated login before any shape is considered.
  me=$(gh api user --jq .login)
  [ -n "$me" ] || { echo "cannot resolve the authenticated gh login" >&2; exit 3; }
  gh api "repos/{owner}/{repo}/issues/$pr/comments" --paginate \
    --jq ".[] | select(.user.login == \"$me\") | .body" |
  grep -E '^Grok check ([1-9]|1[0-2])/12 — (reserved \((full|recheck)\)|released: skipped on limits)$' |
  awk '
    match($0, /^Grok check ([0-9]+)\/12/, m) {
      state[m[1]] = /released/ ? "released" : "reserved"
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

gh pr comment "$pr" --body "$body" >/dev/null
echo "$body"
