#!/usr/bin/env bash
# Does this issue suppress a sweep finding? (#150)
#
# Both sweeps state the rule and neither enforced it: an open issue suppresses a
# candidate only if the REPOSITORY OWNER opened it, and an issue meeting that
# condition is "tracking" while everything else is untracked — so the finding
# files normally rather than being reported as suppressed-but-unclean. That was
# prose in two files, which is a rule a reader follows rather than a control.
# `BothSweepsAgreeOnWhatSuppresses` pinned that both files still SAY it; nothing
# established that a sweep ever applied it to an issue.
#
# **The rule here is authorship alone, and #150 proposed a second condition this
# helper deliberately does not implement.** That issue was written when a
# maintainer's label was also sufficient; a later review round asked what a
# label proves and retired it. A non-collaborator cannot set one at creation, so
# it looks like a maintainer's touch — but it is applied to an issue, not to an
# issue's *contents*, and the author can rewrite the title and body afterwards
# while the label stays. The signal would be "a maintainer once looked at
# something that lived at this number", which is not the claim the gate needs.
# Authorship is not editable. That is the whole of why it is the test, and
# implementing the issue as filed would have reopened what the review closed.
#
# **The owner is RESOLVED, never accepted.** `gh-label-ensure.sh`'s rule: a login
# taken as a parameter is a login a prompt-injected finding gets to choose, and
# the one thing this helper decides is whether to believe an issue. So the only
# argument is the issue number, shape-checked, and the repository comes from the
# checkout the sweep is actually looking at.
#
# **Fail direction.** Suppressing is the dangerous answer — one suppressed
# candidate ends the sweep and reports convergence — so anything this helper
# cannot establish is NOT tracking. A lookup that fails exits 3 rather than 1,
# because "this issue is not the owner's" and "I could not find out" are
# different states and the caller must be able to say which in its summary.
#
# Exit codes, which are the whole interface:
#   0  tracking      — the owner opened it; the candidate may be suppressed
#   1  not tracking  — someone else opened it; the candidate files normally
#   2  usage         — the argument is not an issue number
#   3  undetermined  — the lookup failed; treat as not tracking and say so
set -euo pipefail

[ "$#" -eq 1 ] ||
  { echo "usage: gh-issue-suppresses.sh <issue-number>" >&2; exit 2; }

issue="$1"
# No flags, no `--repo`, no login: an issue number is the entire parameter
# surface, and a number cannot be talked into pointing somewhere else.
[[ "$issue" =~ ^[1-9][0-9]*$ ]] ||
  { echo "issue must be a positive number: $issue" >&2; exit 2; }

owner=$(gh repo view --json owner --jq .owner.login) ||
  { echo "could not resolve the repository owner from this checkout" >&2; exit 3; }
[ -n "$owner" ] ||
  { echo "the repository owner resolved to an empty login" >&2; exit 3; }

# One fixed field set. A caller that could choose fields could ask for the body
# and route untrusted text back through a helper whose job is to keep a decision
# out of the model's hands.
author=$(gh issue view "$issue" --json author --jq '.author.login // ""') ||
  { echo "could not read issue #$issue" >&2; exit 3; }
[ -n "$author" ] ||
  { echo "issue #$issue reports no author; refusing to call it tracking" >&2; exit 3; }

# Printed on both paths, so a suppression is auditable rather than asserted and
# a near miss can be named in the round summary without the caller holding the
# `author` field itself.
if [ "$author" = "$owner" ]; then
  echo "tracking: #$issue was opened by the repository owner ($owner)"
  exit 0
fi

echo "not tracking: #$issue was opened by $author, not the repository owner ($owner)"
exit 1
