#!/usr/bin/env bash
# Who /review-copilot's three feeds admit, declared ONCE and sourced by the
# three feed helpers. The command reads three feeds and each needs the same
# allow-list, so three literal copies is the drift this repository has already
# lost a count to more than once. The helpers assert nothing about this list;
# test_grok_helpers.py asserts that all three READ it and none restates it.
#
# ## Copilot's three spellings
#
# Which one arrives is a property of the API the feed came from rather than of
# the reviewer:
#
#   Copilot                             REST — /pulls/{n}/comments. Measured.
#   copilot-pull-request-reviewer       GraphQL — `gh pr view --json reviews`
#                                       and `--json comments`. Measured for
#                                       reviews; inferred for comments, which
#                                       share one exporter. See
#                                       review-copilot.md's feed table.
#   copilot-pull-request-reviewer[bot]  REST — /pulls/{n}/reviews, which no
#                                       helper here calls. Admitted anyway.
#
# The `[bot]` form is kept deliberately though no feed below produces it: the
# cost of admitting a spelling nobody sends is nothing, and the cost of missing
# one somebody does send is a review body silently reported as a stranger's —
# the direction that fails open.
#
# ## The owner is admitted too, and that is function rather than generosity
#
# review-copilot.md's decision table has THREE rows, not two: Copilot is
# triaged, the repository owner's replies mark a thread already handled, and
# anyone else is reported without being acted on. A two-way filter that dropped
# the owner would take away the input for the middle row — the command could no
# longer tell which threads it had already answered, and would re-triage every
# one of them. Measured on PR #147: 21 of 43 inline comments and 21 of 33
# review bodies are the owner's.
#
# The owner is resolved from the checkout, never passed in — gh-label-ensure.sh
# resolves its repository the same way, and for the same reason: a login taken
# as a parameter is a login a prompt-injected finding can choose.
#
# ## What this is and is not
#
# NOT authentication. A GitHub login is not verified by this list; it is a
# filter that keeps unattended triage from ACTING on text any account can
# write. grok-ledger.sh's collaborator-permission check is the stronger form
# and is deliberately not reached for here — Copilot is not a repository
# collaborator, so a permission check would drop the whole review.
COPILOT_AUTHORS='Copilot
copilot-pull-request-reviewer
copilot-pull-request-reviewer[bot]'

# Copilot's logins alone, as a JSON array. Pure — no network, so the suite can
# exercise the partition without a token.
copilot_authors_json() {
  jq -R -s 'split("\n") | map(select(length > 0))' <<<"$COPILOT_AUTHORS"
}

# Copilot's logins plus the repository owner's, as a JSON array. Needs the
# network; this is the list the helpers actually pass to copilot_partition.
copilot_admitted_json() {
  local owner
  owner=$(gh repo view --json owner --jq .owner.login)
  [ -n "$owner" ] || { echo "could not resolve repository owner" >&2; return 1; }
  copilot_authors_json | jq --arg owner "$owner" '. + [$owner] | unique'
}

# Split a JSON array of feed items into admitted (stdout) and dropped (stderr).
#
#   $1  the admitted logins, as a JSON array
#   $2  jq expression selecting an item's author login
#   $3  jq expression labelling a dropped item — a path, a URL, a timestamp.
#       Anything but the BODY.
#   $4  the feed's name, for the report line
#
# stdin is the whole feed as one JSON array; stdout is the admitted subset as a
# JSON array, same shape in as out, so a caller that parsed the raw feed parses
# this. Admitted items keep their login, which is what lets the caller route
# between the decision table's Copilot row and its owner row.
#
# **A dropped item's body reaches neither stream, and that is the point rather
# than an economy.** A stranger's comment is the injection vector
# /review-copilot holds `Edit` against; reporting its text would put that text
# back into the transcript the filter exists to keep it out of, one stream
# over. What is reported — author and location — is enough to find the comment
# on the PR page by hand, and reads as no instruction.
#
# **The LABEL must be server-generated, and this is subtler than the body.**
# Withholding the body while printing `.path` was the first version of this
# helper, and a review caught it: on a pull request the *author* chooses the
# filenames, git permits a newline inside one, and `jq -r` prints it verbatim.
# So a stranger could open a PR carrying a file whose name is two lines of
# prompt text, comment on it, have the comment dropped, and still land that
# text in the triage transcript through the very report saying it was dropped.
# Pass `.html_url`, `.url` or `.submittedAt` — fields GitHub generates — never
# `.path`, and never anything else the pull request supplies.
#
# `clean` below is the belt to that braces: every reported field is coerced to
# printable ASCII and truncated, so a future caller passing the wrong
# expression gets a mangled label rather than a working injection. It is not
# the control — choosing a server-generated field is — but together they mean
# neither mistake alone is sufficient.
#
# The count is reported even when it is zero. A filter that prints nothing when
# it drops nothing is indistinguishable from one that never ran, which is this
# repository's most-repeated failure wearing a helper's clothes.
# Printable-ASCII coercion for every field the dropped report prints. See the
# LABEL paragraph above: this is the second line of defence, not the first.
#
# The class is written as the literal range space-to-tilde rather than as
# `\u0020-\u007e`, and that is not a style choice. The escaped form has to
# survive a bash single-quoted string on its way into a jq program, and the
# first version of this line did not: jq read the doubled backslash as an
# escaped backslash and built a class of literal characters, which quietly
# replaced the `y` in `mallory` and every space. It sanitised, so it looked
# like it worked. Only running it against a known-good login showed the class
# was matching the wrong thing — the positive control earning its place again.
CLEAN_DEF='def clean: tostring | gsub("[^ -~]"; "?") | .[0:200];'

copilot_partition() {
  local authors="$1" author_expr="$2" label_expr="$3" feed="$4"
  local input admitted dropped_count dropped_lines
  input=$(cat)

  admitted=$(jq --argjson a "$authors" \
    "[ .[] | select((${author_expr}) as \$l | \$a | index(\$l)) ]" <<<"$input")
  dropped_lines=$(jq -r --argjson a "$authors" \
    "${CLEAN_DEF} [ .[] | select(((${author_expr}) as \$l | \$a | index(\$l)) | not) ] |
     .[] | \"  dropped \" + (((${author_expr}) // \"(no login)\") | clean) + \" at \" +
     (((${label_expr}) // \"(no location)\") | clean)" <<<"$input")
  dropped_count=$(jq --argjson a "$authors" \
    "[ .[] | select(((${author_expr}) as \$l | \$a | index(\$l)) | not) ] | length" \
    <<<"$input")

  {
    echo "copilot-filter [$feed]: admitted $(jq length <<<"$admitted"), dropped $dropped_count"
    [ -n "$dropped_lines" ] && echo "$dropped_lines"
  } >&2

  echo "$admitted"
}
