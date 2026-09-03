#!/usr/bin/env python3
"""Judge an edit target by the file it resolves to, not by the path it spells.

**A permission rule matches a SPELLING and an edit lands on a FILE**, and the
gap between those two is #181. `.claude/settings.json` denies
`Edit(.claude/scripts/**)`, and `/review-grok`'s frontmatter denies
`Edit(.claude/**)`, `Edit(.github/**)`, `Edit(deploy/**)` and `Edit(.git/**)`
beside the `src/`, `tests/` and `docs/` the command exists to write. Every one
of those is compared against the path a caller typed. A symbolic link — or, on
Windows, a junction — inside an allowed tree is a spelling no deny matches
while the write lands wherever the link points: inside a denied tree, or out of
the checkout altogether.

**What stood there before was a premise rather than a check.** The helper suite
fails on any tracked mode `120000` in `git ls-files -s`, so `main` carries no
tracked link on any push, and an invocation whose only writers are `Write` and
`Edit` cannot add one. Both halves are true and neither covers the case that
matters: `/review-grok` runs over the *branch* under review, locally, before CI
has said anything about it, and a branch is what introduces files. The premise
is a statement about `main`; the exposure is on the branch.

**So the rule here is one predicate and it names no tree: an edit target must
be the file its path spells.** Resolve the target, re-anchor it on the resolved
checkout root, and refuse it if the two disagree. That refuses a link into the
machinery trees, a link out of the checkout, and every future deny the same
way — this file holds no copy of any deny list, so it cannot go stale as one
changes, and it cannot lock the repository out of its own control surface
either: an edit spelled at the file it actually is passes here and is then
judged by the rules that already exist.

**Two things follow from the anchoring that are worth stating before someone
reads a false positive as a bug.** The checkout root is itself resolved, so a
worktree under a temp root that is a link — `/tmp` on macOS, an 8.3 or `subst`
path on Windows — is judged against its own real spelling rather than refused
wholesale. And the comparison is case-insensitive where the platform is:
Windows' `realpath` returns the on-disk case, so `DOCS/x` would otherwise
differ from `docs/x` and be refused for a difference that is not one.

**The residual, stated rather than left to be found.** The subject is a target
spelled *inside* a checkout this session is standing in. A path spelled
entirely outside one — an absolute path into a scratch directory, or into the
user's own `~/.claude` — is not judged here, because the harness writes its own
state that way and refusing it would take the session's memory and scratchpad
with it. Nothing in the exposure this closes can spell one: `/review-grok`'s
site contract admits one plain repository-relative path per row, with no
leading slash, no drive letter and no `..` segment, and the adjudicator drops a
row that is not. A guard for the out-of-tree half would have to be a rule about
which out-of-tree paths are legitimate, which is a different file's argument.

Protocol: PreToolUse, matcher `Edit|Write|NotebookEdit|MultiEdit`. Exit 0 and
print nothing to allow; print the deny JSON to refuse. The JSON form carries a
reason the caller can read, and a guard that refuses without saying why is one
that gets worked around rather than fixed — `guard-git-argv.py` argues the same
choice and this file follows it.
"""

import json
import os
import sys

# The tools that write a file. `MultiEdit` is listed although this repository's
# harness does not surface it: the matcher in `.claude/settings.json` is a
# regular expression over the tool name, so a tool that is registered later
# arrives here judged rather than unjudged, and a name this file does not know
# is refused by the `EDITING_TOOLS` test below only after the matcher has
# already let it through.
EDITING_TOOLS = ("Edit", "Write", "NotebookEdit", "MultiEdit")

# Where each of those carries its target. `Edit` and `Write` use `file_path`;
# `NotebookEdit` uses `notebook_path`. Both are read, and a matched call
# carrying neither is refused rather than waved through — a write whose target
# this file cannot see is one it has established nothing about.
PATH_KEYS = ("file_path", "notebook_path")


def same(left, right):
    """Whether two absolute paths name the same place on this platform."""
    return os.path.normcase(os.path.normpath(left)) == os.path.normcase(
        os.path.normpath(right))


def under(child, parent):
    """Whether `child` is `parent` or sits beneath it, lexically."""
    child = os.path.normcase(os.path.normpath(child))
    parent = os.path.normcase(os.path.normpath(parent))
    if child == parent:
        return True
    if not parent.endswith(os.sep):
        parent += os.sep
    return child.startswith(parent)


def anchors(event):
    """The checkouts this guard is standing in, as (spelled, resolved) pairs.

    **Three sources, because no one of them is right in every session.**
    `CLAUDE_PROJECT_DIR` is what the harness sets and what `.claude/settings.json`
    interpolates into this hook's own command line; the event's `cwd` is where
    the session actually is, which differs the moment `/branch` moves it into a
    sibling worktree; and this file's own location is the checkout that owns
    the guard, which is true even if the other two are absent or wrong.

    Each is kept as the pair it is — the spelling and its resolution — because
    the whole judgement below is a comparison between those two, and an anchor
    reached through a link would otherwise make every edit under it look like
    the thing this file refuses.
    """
    here = os.path.dirname(os.path.dirname(os.path.dirname(
        os.path.abspath(__file__))))
    found = []
    for path in (os.environ.get("CLAUDE_PROJECT_DIR"), event.get("cwd"), here):
        if not path or not isinstance(path, str):
            continue
        spelled = os.path.abspath(path)
        if any(same(spelled, seen) for seen, _ in found):
            continue
        found.append((spelled, os.path.realpath(path)))
    return found


def offence(event):
    """The reason to refuse this call, or `None` to let it through."""
    tool = event.get("tool_name")
    if tool not in EDITING_TOOLS:
        return None

    # A `tool_input` that is not an object goes to the same refusal as one
    # carrying no path, and the two used to differ: the shape check returned
    # `None` — admit — while the missing key refused. They are the same
    # statement about the same call, which is that this file cannot see where
    # the write lands, and only one of the two answers is the one that fails
    # closed.
    tool_input = event.get("tool_input")
    spelled = None
    if isinstance(tool_input, dict):
        for key in PATH_KEYS:
            value = tool_input.get(key)
            if isinstance(value, str) and value:
                spelled = value
                break
    if spelled is None:
        return (
            f"guard-edit-target: {tool} carries no file path this guard can "
            "read, so nothing has been established about where it writes. "
            "Refusing rather than waving it through."
        )

    cwd = event.get("cwd")
    if not isinstance(cwd, str) or not cwd:
        cwd = os.getcwd()

    # `realpath` is taken of the ORIGINAL spelling and `normpath` of the joined
    # one, and the order matters: `normpath` collapses `..` lexically, which is
    # the wrong answer for a `..` that follows a link, so the lexical form is
    # used only to locate the target under an anchor. Where the two disagree
    # the call is refused, which is the direction this has to fail in.
    joined = spelled if os.path.isabs(spelled) else os.path.join(cwd, spelled)
    lexical = os.path.normpath(os.path.abspath(joined))
    resolved = os.path.realpath(joined)

    refusal = None
    for spelled_root, real_root in anchors(event):
        if under(lexical, spelled_root):
            base = spelled_root
        elif under(lexical, real_root):
            base = real_root
        else:
            continue

        expected = os.path.normpath(
            os.path.join(real_root, os.path.relpath(lexical, base)))
        if same(resolved, expected):
            return None
        if refusal is None:
            escaped = not under(resolved, real_root)
            where = "outside the checkout" if escaped else "elsewhere in it"
            refusal = (
                f"guard-edit-target: {spelled} resolves {where} — to "
                f"{resolved}. A permission rule matches the path as written, "
                "so an edit through a link lands where no deny has judged it. "
                "Write the file at its real path, or say why the link is "
                "there (#181, docs/harness-boundaries.md)."
            )

    # A target under no anchor at all is not this guard's subject; the module
    # docstring argues why, and a test pins the residual so the next reader
    # does not have to take the paragraph's word for it.
    return refusal


def main():
    try:
        event = json.loads(sys.stdin.buffer.read().decode("utf-8"))
    except (json.JSONDecodeError, ValueError, UnicodeDecodeError):
        # The one deliberate fail-OPEN, and it is the argv guard's argument
        # rather than a second decision: a hook that cannot read its own input
        # has established nothing, and refusing every write on a malformed
        # event would turn a defect in this file into a dead session.
        print("guard-edit-target: unreadable hook event; not judging",
              file=sys.stderr)
        return 0

    if not isinstance(event, dict):
        print("guard-edit-target: hook event is not an object; not judging",
              file=sys.stderr)
        return 0

    reason = offence(event)
    if reason is None:
        return 0

    json.dump(
        {
            "hookSpecificOutput": {
                "hookEventName": "PreToolUse",
                "permissionDecision": "deny",
                "permissionDecisionReason": reason,
            }
        },
        sys.stdout,
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
