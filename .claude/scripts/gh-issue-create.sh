#!/usr/bin/env bash
# File one issue on THIS repository from a sweep, and nothing else: the kind
# and severity labels from a fixed vocabulary on the command line, the title
# and the body from stdin, and the repository from the checkout this script is
# standing in.
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
# out of a closed set, and the title and body are bytes on stdin — the form
# both sweeps already used for the body, because an inline `--body` mangles
# the wrapping and a temp file would need the `Write` grant the sweeps
# withhold.
#
# THE TITLE IS ON STDIN, NOT THE COMMAND LINE, and the reason is where it
# comes from. A sweep's title is composed from a verdict record, and a record
# is text a crafted tree can steer. As an argument it crossed the parent's
# shell before this script saw it: `bash gh-issue-create.sh "$(…)" …` runs the
# substitution in the parent, and no check here can run first. Inside a quoted
# heredoc nothing is expanded, so the title travels the same channel as the
# body and arrives as the bytes the parent wrote. The first line of stdin is
# the title, the second is blank, and the rest is the body.
#
# THE LAST LINE IS FIXED, AND IT IS A DETECTOR RATHER THAN A GUARD. A quoted
# heredoc has one thing the payload can still steer: its terminator. The body
# quotes repository lines, so a repository line equal to the delimiter closes
# the heredoc early and hands the rest of the payload to the parent's shell as
# commands. Nothing in this script can stop that — it runs after the parent
# has parsed — so the guard is the sweeps' rule that the delimiter is a token
# checked against every line of the payload before the command is composed.
# What this script can do is refuse to file what an early close leaves behind:
# the body must end with the trailer line below, exactly, and a body cut short
# has lost it. A truncated filing is then a loud exit 2 rather than a
# half-issue with its second half executed.
#
# THE MSYS TITLE HAZARD CLOSES HERE TOO, and this is the one thing a helper
# can do that a grant could not. MSYS argument conversion rewrites an argument
# that looks like an absolute POSIX path before a native `gh.exe` sees it, so a
# title beginning with `/` filed four times as `C:/Program Files/Git/...`
# (#55, #56, #68). The commands could not set MSYS2_ARG_CONV_EXCL, because an
# env-prefixed command no longer begins with `gh issue create` and the grant is
# a prefix match. A script sets it for its own child and the grant on the
# script is unchanged.
# THE FIXED LINE IS SELECTED, NOT CONSTANT, and #184 is why. A detector needs
# a last line the caller cannot lose by accident; it does not need that line to
# be the SAME line for everybody. What stood here was one sentence asserting
# provenance — that a sweep filed the issue, and that a second read-only
# auditor confirmed it before filing — required of every body this helper
# takes. The helper is the only sanctioned route, both sweeps deny raw
# `gh issue create` by name, and `CLAUDE.md` sends every route through here, so
# an issue filed by hand out of a review triage or a measurement taken mid-PR
# was made to claim a provenance it did not have. Measured: #183 was filed that
# way and carried the sentence until it was edited afterwards.
#
# A claim every issue makes is a claim that distinguishes nothing, which is the
# one thing this sentence was for: a sweep's issue says the finding was
# confirmed by a second read-only agent, and that is worth knowing when
# triaging something nobody has reproduced. The same shape as a severity stated
# in prose that nothing can filter on. So the route is a parameter, out of a
# closed set like the other two, and each spelling ends the body with the
# sentence that is true of it. The detector is unchanged — the line is still
# fixed, still exact, and a body cut short has still lost it.
set -euo pipefail

[ "$#" -eq 3 ] ||
  { echo "usage: gh-issue-create.sh <security|bug> <critical|high|medium|low> <sweep|hand> < title, blank line, body ending in the trailer" >&2; exit 2; }
kind="$1"; severity="$2"; route="$3"

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
# The route decides the fixed last line, and it is the same closed-set test as
# the other two: a spelling outside the set is refused rather than defaulted,
# because a default is how the unconditional claim would come back. There is
# deliberately no third spelling for "filed by a sweep whose auditor did not
# run" — a sweep that cannot verify a finding does not file it.
case "$route" in
  sweep) trailer='Filed by an authorised sweep and verified at filing by a second read-only auditor.' ;;
  hand) trailer='Filed by hand rather than by a sweep: no second auditor verified it at filing.' ;;
  *) echo "not a route this helper will file under: $route" >&2; exit 2 ;;
esac

# The title is the first line of stdin and the second line must be blank, so a
# body that arrives without its title line is refused rather than filed under
# its own first sentence. An empty title files an issue nobody can find in the
# tracker; a title cannot contain a newline, because a line is what it is. A
# stdin that ends before the second line is refused too: `read` fails at EOF,
# and an unset separator is not a blank one.
IFS= read -r title || true
title="${title%$'\r'}"
[ -n "$title" ] || { echo "the title is empty" >&2; exit 2; }
IFS= read -r separator ||
  { echo "stdin ended before the blank line: title, blank line, body" >&2; exit 2; }
separator="${separator%$'\r'}"
[ -z "$separator" ] ||
  { echo "the second line of stdin must be blank: title, blank line, body" >&2; exit 2; }

# The body is what is left of stdin, and it is read whole here rather than
# streamed, because its last line is checked before anything is filed.
body=$(cat; printf x); body="${body%x}"
last=$(printf '%s' "$body" | sed -e 's/\r$//' -e '/^[[:space:]]*$/d' | tail -n 1)
[ "$last" = "$trailer" ] ||
  { echo "the body does not end with the trailer line; a heredoc closed early or the line was left off" >&2; exit 2; }

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

# `--body-file -` reads bytes, so the body goes back out on stdin unchanged.
# The title is the one argument the conversion below could ever have touched,
# and it is excluded for this child.
printf '%s' "$body" | MSYS2_ARG_CONV_EXCL='*' gh issue create \
  --repo "$repo" \
  --title "$title" \
  --label "$kind" \
  --label "$severity" \
  --body-file -
