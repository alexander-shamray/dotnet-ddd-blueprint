#!/usr/bin/env bash
# Writes one line of the Grok check ledger to a PR, and can write nothing else.
#
# The ledger could have been a Bash(gh pr comment:*) grant, and briefly was —
# but a Bash rule matches a command prefix, so that grant also licensed
# --edit-last, --delete-last, --body-file and --repo: editing existing
# comments, deleting them, and writing to other repositories, none of which
# the ledger needs and none of which anyone reviewed. Same argument as
# copilot-request.sh one file over: the helper fixes the operation and
# shape-checks its parameters, the frontmatter grants the helper by name, and
# .claude/settings.json's Edit deny on this directory keeps the session that
# invokes it from rewriting it first.
#
# Write-only on purpose. Reads ride the already-granted gh pr view — a resumed
# /ship recovers the count as the highest N reserved and not released — so the
# only capability this file adds is posting the two fixed lines below to a PR
# of this repository.
set -euo pipefail

usage() {
  echo "usage: grok-ledger.sh <pr-number> reserve <n> <full|recheck>" >&2
  echo "       grok-ledger.sh <pr-number> release <n>" >&2
  exit 2
}

pr="${1:-}"
op="${2:-}"
n="${3:-}"
mode="${4:-}"

# The PR number keeps gh pointed at an explicit target, and N's domain is the
# ledger's whole vocabulary: twelve checks, so 1..12 and nothing else.
[[ "$pr" =~ ^[0-9]+$ ]] || usage
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
