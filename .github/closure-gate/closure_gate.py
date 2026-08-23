#!/usr/bin/env python3
"""What a pull request says it closes must match what merging it will close.

It says it three times, and the three are honoured by different machinery,
which is why they can disagree without anybody noticing:

1. **The `| Closes |` row** in the house body form — a human-readable summary,
   read by people and by nothing else.
2. **`closingIssuesReferences`** — GitHub's own answer to "what will merging
   this close", parsed out of the pull request *body*.
3. **A closing keyword in a commit body** — honoured on merge regardless of
   what the description says, and unlike the description it cannot be edited.

Both directions have fired on this repository:

* **Under-closing, PR #112.** Its keywords lived only in the table cell.
  `closingIssuesReferences` reads `[]` to this day; #84, #70 and #40 stayed
  open after the merge and were closed by hand. A cell boundary sits between
  `Closes` and `#88` in `| Closes | #88 |`, so it is not a keyword-reference
  pair at all — GitHub is not declining to read a table, it is being handed
  two cells.
* **Over-closing, PR #116.** The review loop narrowed two claims and the body
  was rewritten to say "#56 stays open". The merge closed #30 and #56 anyway,
  out of commits written before the loop ran. Measured, not recalled: that
  PR's commits carry `{30, 31, 32, 55, 56}` and its
  `closingIssuesReferences` reports `{31, 32, 55}`. The set difference is
  precisely the pair that had to be reopened by hand.

The second is the one no amount of care in the description reaches, because
`gh pr view --json closingIssuesReferences` reports the body only — so the
discrepancy is invisible from the one place a reviewer would look.

**One direction is deliberately not compared, and the first line above reads
as though it were.** An issue the description closes and no commit body
mentions is the ordinary case, not a disagreement: the bare `Closes #n` line
under the table is what fires, and a commit is not obliged to repeat it.
Adding the symmetric `linked - from_commits` check would make a commit
keyword *mandatory* — a rule nothing in this repository states, and one that
would fail a correct pull request. What has to agree is what the merge
**does** — `closingIssuesReferences` together with the commit keywords — and
what the pull request **says** it does, in the table and in the description.
A silent commit contradicts neither. The `NoCommitRepeatsIt` case in
`test_closure_gate.py` pins it, so the fourth comparison cannot be added by
someone reading the three statements above as three pairs that have to
match. This file's opening sentence *was* that reading — it said the three
statements must agree — and it was flagged from three separate sites before
being rewritten rather than annotated.

**Half of this comparison is GitHub's own parse and half is the regex below,
and that asymmetry decides the failure mode.** A regex that matches too much
makes the gate disagree with GitHub and fail loudly, which is recoverable. A
regex that matches too *little* drops a commit keyword out of the commit set;
if the description omits it too, the sets agree and the gate passes while the
merge closes an issue nobody declared. That is fail-open, and it is this
repository's most-repeated failure wearing a new hat. So the parser is
deliberately literal — it matches inside backticks and inside quoted prose,
exactly as GitHub's linker does — and anything keyword-shaped it cannot
resolve to a number is reported as a problem rather than skipped.

**The commit half arrives one page at a time, and a short list looks exactly
like a complete one.** `gh pr view --json commits` returns a single page, so a
pull request longer than that page hands this gate a prefix of its own
history — and a closing keyword in a commit past the cut is absent from the
commit set for a reason that has nothing to do with what the pull request
says. If the description omits it too, the sets agree and the merge closes an
issue nobody declared: the fail-open shape this file exists to close, reached
by a route the parser cannot see. So `GH_PAGE_SIZE` makes it fail closed — a
list at or above the page size is **refused rather than judged**, and the
message says to fetch through a paginated endpoint.

**`closingIssuesReferences` is NOT exposed to that, and a guard for it was
added here and then removed.** `gh` preloads the collection: `finder.go`
dispatches to `preloadPrClosingIssuesReferences`, which loops on
`PageInfo.HasNextPage` issuing `closingIssuesReferences(first: 100, after:
$endCursor)` until it is exhausted. Commits get no such treatment — the
preload set is reviews, comments, closing issues and checks, and nothing
else — which is exactly why one of these two collections needs a guard and
the other must not have one. A guard there would refuse every pull request
with a hundred or more linked issues, and its own advice would be
unfollowable, because that fetch is already paginated.

**Measured against cli/cli at v2.92.0, not assumed.** An earlier revision of
this docstring said the page size was "GitHub's documented default and has
not been measured here" and guarded both collections on that guess. Half of
it was right, and the wrong half was a false-refusal generator sitting in a
gate whose whole subject is not trusting an unchecked claim. The guard
triggers at or above the size rather than on equality for the reason that
still holds: a prefix of exactly one page and a complete list of exactly one
page are indistinguishable from in here. `NoLinkedGuard` in the suite pins
the removal, so the symmetric guard cannot come back on the symmetry
argument that produced it.

Stdlib only, on the licence gate's terms, and the network is not in here: the
deciding takes JSON on stdin and the fetching is one `gh` call in the
workflow. That is `deploy/canary/canary.py`'s split, for its reason.

    gh pr view <n> --json number,url,body,commits,closingIssuesReferences |
        py -3.12 .github/closure-gate/closure_gate.py

**Cross-repository closing is out of scope and is refused rather than
ignored.** Every issue this repository tracks lives in this repository, so a
`Closes owner/other#12` is either a mistake or a case nobody has thought
about; either way the gate names it instead of dropping it.
"""

from __future__ import annotations

import argparse
import json
import re
import sys

# GitHub's documented set, all nine spellings. Written out rather than
# generated, because a generated list is one more thing that can quietly
# produce eight.
_KEYWORD = r"(?:close[sd]?|fix(?:es|ed)?|resolve[sd]?)"

# Every keyword followed by whatever token comes next. Classification happens
# afterwards, and that ordering is the point: a keyword whose reference this
# gate cannot parse is a finding, not a non-match. Matching the reference
# shape here instead would turn every unhandled spelling into silence.
KEYWORD_THEN_TOKEN = re.compile(rf"\b{_KEYWORD}\b\s*:?\s+(?P<token>\S+)", re.IGNORECASE)

# What a token has to look like to be an issue reference. The optional
# `owner/repo` prefix is captured so a cross-repository form can be refused by
# name rather than failing to match and vanishing.
REFERENCE = re.compile(
    r"^(?:(?P<repo>[\w.-]+/[\w.-]+))?#(?P<number>\d+)\b"
    r"|^https?://github\.com/(?P<url_repo>[\w.-]+/[\w.-]+)/issues/(?P<url_number>\d+)\b",
    re.IGNORECASE,
)

# Markup a reference may be wrapped in. A commit body arguing about a keyword
# writes it in backticks, and GitHub links it anyway.
WRAPPERS = "`\"'([{*_<"

# The house form's metadata row: `| Closes | #88 (high), #81 (high) |`.
TABLE_ROW = re.compile(
    rf"^[ \t]*\|[ \t]*{_KEYWORD}[ \t]*\|(?P<cell>[^|]*)\|",
    re.IGNORECASE | re.MULTILINE,
)

ISSUE_NUMBER = re.compile(r"#(\d+)\b")

REQUIRED_FIELDS = ("number", "url", "body", "commits", "closingIssuesReferences")

# One page of `gh pr view`. The commit list is not preloaded, so a list this
# long may be a prefix and a prefix is indistinguishable from the whole thing
# from in here. `closingIssuesReferences` IS preloaded and is deliberately
# not measured against this — see the docstring.
GH_PAGE_SIZE = 100

PULL_URL = re.compile(r"^https?://github\.com/(?P<repo>[\w.-]+/[\w.-]+)/pull/\d+", re.IGNORECASE)


def repository_of(pull_url: str) -> str | None:
    """`owner/name` from the pull request's own URL, so nothing has to be passed in."""
    match = PULL_URL.match(pull_url.strip())
    return match.group("repo") if match else None


def closing_references(text: str, repository: str) -> tuple[set[int], list[str]]:
    """Issue numbers a closing keyword in `text` names, and what could not be read.

    Returns `(numbers, unreadable)`. A keyword followed by ordinary prose —
    "closes the naive spelling and nothing more" — is neither: it is not a
    reference and it is not a failure to read one, so it appears in neither
    half. Only a token that *looks* like a reference and still does not resolve
    to an issue in this repository is reported.
    """
    numbers: set[int] = set()
    unreadable: list[str] = []

    for match in KEYWORD_THEN_TOKEN.finditer(text):
        token = match.group("token").lstrip(WRAPPERS)
        reference = REFERENCE.match(token)
        if reference is None:
            # Prose. `Closes the door` is English, not a link.
            if token.startswith("#") or "github.com/" in token.lower():
                unreadable.append(match.group(0).strip())
            continue

        named = reference.group("repo") or reference.group("url_repo")
        number = reference.group("number") or reference.group("url_number")
        if named is not None and named.lower() != repository.lower():
            unreadable.append(match.group(0).strip())
            continue
        numbers.add(int(number))

    return numbers, unreadable


def declared_in_table(body: str) -> set[int]:
    """Issue numbers named by the body's `| Closes |` metadata row."""
    return {
        int(number)
        for row in TABLE_ROW.finditer(body)
        for number in ISSUE_NUMBER.findall(row.group("cell"))
    }


def check(payload: dict) -> list[str]:
    problems: list[str] = []

    missing = [field for field in REQUIRED_FIELDS if field not in payload]
    if missing:
        return [
            "the gate was handed JSON with no "
            + ", ".join(f"`{field}`" for field in missing)
            + " — it judged nothing, and a gate that judged nothing must not report a pass"
        ]

    repository = repository_of(payload["url"] or "")
    if repository is None:
        return [f"`url` is not a pull request URL, so the repository is unknown: {payload['url']!r}"]

    commits = payload["commits"]
    if not commits:
        return [
            "this pull request reports no commits at all, which is not a state "
            "the gate can judge — the commit half of the comparison would be "
            "empty for the wrong reason"
        ]

    if len(commits) >= GH_PAGE_SIZE:
        return [
            f"this pull request reports {len(commits)} commits, at or above "
            f"the {GH_PAGE_SIZE} that `gh pr view --json commits` returns "
            f"in one page — so the list may be a prefix, and a closing keyword "
            f"past the cut is invisible to this gate for a reason that has "
            f"nothing to do with what the pull request says. That is the "
            f"fail-open shape this gate exists to close, so it refuses rather "
            f"than judging: fetch the commits through a paginated endpoint and "
            f"pass the complete list"
        ]

    linked = {issue["number"] for issue in payload["closingIssuesReferences"]}
    declared = declared_in_table(payload["body"] or "")

    from_commits: dict[int, list[str]] = {}
    for commit in commits:
        oid = (commit.get("oid") or "")[:8] or "(no oid)"
        text = f"{commit.get('messageHeadline', '')}\n{commit.get('messageBody', '')}"
        numbers, unreadable = closing_references(text, repository)
        for number in numbers:
            from_commits.setdefault(number, []).append(oid)
        for phrase in unreadable:
            problems.append(
                f"commit {oid} carries `{phrase}`, which is keyword-shaped and "
                f"names no issue in {repository} — this gate will not guess at it"
            )

    # The body is read only for the keyword-shaped tokens below. What the
    # description *closes* is `closingIssuesReferences`, which is GitHub's own
    # answer rather than this file's, and there is no reason to prefer a second
    # opinion to the authority.
    _, body_unreadable = closing_references(payload["body"] or "", repository)
    for phrase in body_unreadable:
        problems.append(
            f"the description carries `{phrase}`, which is keyword-shaped and "
            f"names no issue in {repository} — this gate will not guess at it"
        )

    for number in sorted(from_commits.keys() - linked):
        oids = ", ".join(sorted(from_commits[number]))
        problems.append(
            f"#{number} is closed by commit {oids} and the description does not "
            f"name it (`closingIssuesReferences` reports "
            f"{sorted(linked) or 'nothing'}). The merge will close it either "
            f"way — a commit message cannot be edited — so reconcile the "
            f"description to the commits, not the commits to the description"
        )

    for number in sorted(declared - linked):
        problems.append(
            f"the `| Closes |` row names #{number} and GitHub linked nothing "
            f"for it. A table pipe between the keyword and the reference means "
            f"there is no keyword-reference pair to read: add a bare "
            f"`Closes #{number}` line below the table"
        )

    for number in sorted(linked - declared):
        problems.append(
            f"merging closes #{number} and the `| Closes |` row does not say "
            f"so, so the summary a reader trusts understates what happens"
        )

    return problems


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "payload",
        nargs="?",
        type=argparse.FileType(encoding="utf-8"),
        default=sys.stdin,
        help="`gh pr view --json ...` output; stdin by default",
    )
    args = parser.parse_args(argv[1:])

    try:
        payload = json.load(args.payload)
    except json.JSONDecodeError as error:
        print(f"closure-gate: the input is not JSON: {error}", file=sys.stderr)
        return 2

    problems = check(payload)
    if problems:
        print(
            f"closure-gate: {len(problems)} problem(s) with what PR "
            f"#{payload.get('number', '?')} says it closes:\n",
            file=sys.stderr,
        )
        for problem in problems:
            print(f"  - {problem}", file=sys.stderr)
        return 1

    linked = sorted(issue["number"] for issue in payload["closingIssuesReferences"])
    closes = ", ".join(f"#{number}" for number in linked) if linked else "nothing"
    print(
        f"closure-gate: what this pull request says it closes matches what "
        f"merging it will close — {closes}."
    )
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
