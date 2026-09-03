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

**Three things follow from the anchoring that are worth stating before someone
reads a false positive as a bug.** The checkout root is itself resolved, so a
worktree under a temp root that is a link — `/tmp` on macOS, an 8.3 or `subst`
path on Windows — is judged against its own real spelling rather than refused
wholesale. An anchor is a checkout **root** and every anchor containing the
target must agree, because an anchor excuses one link traversal — its own root
prefix — so an anchor at a linked directory inside the tree excuses precisely
what this file refuses. And the comparison folds case where the **filesystem**
does, asked of the filesystem by `case_insensitive` rather than read off the
platform: Windows' `realpath` returns the on-disk case, so `DOCS/x` would
otherwise differ from `docs/x` and be refused for a difference that is not one
— and macOS mounts APFS case-insensitively by default, where a platform test
folds nothing and a differently-cased checkout prefix matches no anchor at
all, which is the branch that admits. A case-insensitive mount on Linux is the
same case again, which is why the question is asked of the mount.

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
import unicodedata

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

# **Windows names the same file in more than one alphabet, and a permission
# rule reads only one of them.** Every spelling beginning `\\` is the other
# one: the extended-length prefix `\\?\` and the device prefix `\\.\`, which
# exist precisely to SKIP the normalisation a matcher depends on, and the UNC
# form `\\server\share\...`, which reaches the local disk through the
# administrative shares. **Both were measured in this checkout with
# `.claude/sandbox/**` denied, and both were CREATED**: a `Write` to
# `\\?\C:\dev\ashamray\.claude\sandbox\probe-unc.txt` and one to
# `\\localhost\C$\dev\ashamray\.claude\sandbox\probe-share.txt`. The plain
# spelling of either file is refused. Both probe files were deleted.
#
# So the whole family is refused rather than the two prefixes that were found
# first — enumerating spellings is the deny-list shape this repository has
# rejected twice, and the UNC form is what a list of prefixes would have
# missed. Refused rather than resolved, because a hook can only allow or deny:
# it cannot hand the matcher the plain spelling it would have judged.
#
# **Unless a checkout is itself named that way**, which is the one legitimate
# case — a repository on a network share. Then the anchors are `\\`-spelled
# too, the matcher's strings and the guard's agree, and nothing here fires.
# Scoped to Windows because no other platform has a second alphabet: `//x` on
# POSIX is an ordinary path, and refusing it would be a rule about nothing.
def alternate_alphabet(path):
    """Whether `path` is spelled in Windows' non-drive path grammar."""
    return os.name == "nt" and path[:2].replace("/", "\\") == "\\\\"


def case_insensitive(path):
    """Whether `path`'s filesystem resolves a differently-cased spelling to it.

    **`os.path.normcase` folds case on Windows and nowhere else**, and that is
    a statement about the PLATFORM where what matters is the FILESYSTEM. macOS
    mounts APFS case-insensitively by default, so `/Users/x/Repo` and
    `/users/x/repo` are one directory there while `normcase` leaves them
    different strings — and a comparison built on it decides the target is
    under no anchor at all, which is the branch that admits. Raised by Copilot;
    the same is true of a case-insensitive mount on Linux.

    Asked of the filesystem rather than read off `sys.platform`: a component of
    the path is case-flipped and both spellings are `stat`ed, and one file with
    one device and inode under two spellings is the answer.

    **Which component is not a detail, and picking the basename alone was a
    gap.** A checkout at `/Users/me/123` has nothing to flip in its last
    component, so the probe fell to the platform default — `False` on macOS,
    where the mount folds — and a linked target spelled `/users/me/123/...`
    matched no anchor and fell through unjudged. Raised by Copilot. Every
    component is tried, deepest first, and the first one that changes under
    `swapcase` carries the probe; a path with no cased component anywhere is
    the only case left to the platform default, and it cannot arise under a
    root that holds a `.git`.
    """
    normalised = os.path.normpath(path)
    parts = normalised.split(os.sep)
    for index in range(len(parts) - 1, -1, -1):
        flipped = parts[index].swapcase()
        if flipped == parts[index]:
            continue
        other = os.sep.join(parts[:index] + [flipped] + parts[index + 1:])
        try:
            here = os.stat(normalised)
            there = os.stat(other)
        except OSError:
            return False
        return (here.st_dev, here.st_ino) == (there.st_dev, there.st_ino)
    return os.name == "nt"


def key(path, folded):
    """One comparable spelling of `path`, folded where the filesystem folds.

    **Unicode is composed unconditionally, where case is folded only where the
    mount folds it.** A case-insensitive APFS volume is *also* insensitive to
    normalisation, so `é` composed and `e` + a combining accent name one
    directory there while they are two strings here — a checkout prefix spelled
    in the other form matched no anchor and reached the branch that admits.
    Raised by Copilot.

    It needs no probe, unlike the case question, because composing costs
    nothing where the filesystem does distinguish the two: two genuinely
    different files still differ in every component that is not a
    normalisation of the other, so this can make two spellings of *one* path
    compare equal and can never make two paths look like one.
    """
    spelling = unicodedata.normalize(
        "NFC", os.path.normcase(os.path.normpath(path)))
    return spelling.lower() if folded else spelling


def same(left, right, folded):
    """Whether two absolute paths name the same place."""
    return key(left, folded) == key(right, folded)


def under(child, parent, folded):
    """Whether `child` is `parent` or sits beneath it, lexically."""
    child = key(child, folded)
    parent = key(parent, folded)
    if child == parent:
        return True
    if not parent.endswith(os.sep):
        parent += os.sep
    return child.startswith(parent)


def checkout_root(path):
    """The nearest ancestor of `path` holding a `.git`, or `None`.

    **An anchor has to be a checkout ROOT rather than any directory the session
    happens to stand in**, and the difference is a bypass rather than a
    nicety. An anchor excuses exactly one link traversal — the one on its own
    root prefix — so an anchor at `<checkout>/docs/tree`, where `tree` links
    into `.claude/scripts`, excuses precisely the traversal this file exists
    to refuse: the target's spelling is `docs/tree/helper.sh`, its resolution
    is `.claude/scripts/helper.sh`, and re-anchoring on that directory makes
    the two agree. Raised by Copilot against the first form, which took the
    event's `cwd` as an anchor whatever it pointed at.

    Walked lexically from the spelling, which is what makes it the right root
    for the case above: `<checkout>/docs/tree` walks to `<checkout>/docs` and
    then to `<checkout>`, where the `.git` is. A worktree's `.git` is a file
    rather than a directory, so this asks whether the entry exists at all.
    """
    current = os.path.abspath(path)
    while True:
        if os.path.exists(os.path.join(current, ".git")):
            return current
        parent = os.path.dirname(current)
        if parent == current:
            return None
        current = parent


def anchors(event):
    """The checkouts this guard is standing in, as (spelled, resolved) pairs.

    **Three sources, because no one of them is right in every session.**
    `CLAUDE_PROJECT_DIR` is what the harness sets and what
    `.claude/settings.json` interpolates into this hook's own command line; the
    event's `cwd` is where the session actually is, which differs the moment
    `/branch` moves it into a sibling worktree; and this file's own location is
    the checkout that owns the guard, which is true even if the other two are
    absent or wrong.

    The first and the last are roots by construction — the harness sets one to
    a project root and the other is this file's own tree — so they are taken as
    given. `cwd` is not: it is wherever the session stands, so it is walked up
    to its checkout root and **dropped** when it has none, because a directory
    that belongs to no checkout is not a root and excusing a traversal at it is
    the bypass `checkout_root` documents.

    Each is kept as the pair it is — the spelling and its resolution — because
    the whole judgement below is a comparison between those two, and an anchor
    reached through a link would otherwise make every edit under it look like
    the thing this file refuses.

    **Adding an anchor can only narrow this guard, never widen it**, because
    the caller requires every anchor containing the target to agree. That is
    what makes an environment-supplied `CLAUDE_PROJECT_DIR` safe to trust here:
    a wrong one cannot excuse a traversal that this file's own tree refuses.
    """
    here = os.path.dirname(os.path.dirname(os.path.dirname(
        os.path.abspath(__file__))))
    cwd = event.get("cwd")
    roots = [
        os.environ.get("CLAUDE_PROJECT_DIR"),
        checkout_root(cwd) if isinstance(cwd, str) and cwd else None,
        here,
    ]
    found = []
    for path in roots:
        if not path or not isinstance(path, str):
            continue
        spelled = os.path.abspath(path)
        folded = case_insensitive(spelled)
        if any(same(spelled, seen, folded) for seen, _, _ in found):
            continue
        found.append((spelled, os.path.realpath(path), folded))
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
    # `name` rather than `key`, which is a function in this module: nothing
    # here calls it after the loop, so the shadow was harmless and the next
    # edit is what it would have cost.
    tool_input = event.get("tool_input")
    spelled = None
    if isinstance(tool_input, dict):
        for name in PATH_KEYS:
            value = tool_input.get(name)
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

    checkouts = anchors(event)
    if alternate_alphabet(spelled) and not any(
            alternate_alphabet(root) for root, _, _ in checkouts):
        return (
            f"guard-edit-target: {spelled} is spelled in Windows' other path "
            "grammar — an extended-length or device prefix, or a UNC share — "
            "while every checkout this session stands in is named by an "
            "ordinary path. A permission rule matches the string it is given, "
            "and measured here a denied directory accepted a write spelled "
            "both of those ways. Name the file the way the rules are written."
        )

    # `realpath` is taken of the ORIGINAL spelling and `normpath` of the joined
    # one, and the order matters: `normpath` collapses `..` lexically, which is
    # the wrong answer for a `..` that follows a link, so the lexical form is
    # used only to locate the target under an anchor. Where the two disagree
    # the call is refused, which is the direction this has to fail in.
    #
    # **A `..` that traverses no link is admitted here, and that is a decision
    # backed by a measurement rather than an oversight.** The argument for
    # refusing it is that `docs/../.claude/hooks/x` carries no `.claude/**`
    # spelling, so a matcher reading the string would not deny it — and the
    # harness does not read the string. Measured in this checkout, with
    # `.claude/sandbox/**` denied: a `Write` to
    # `docs/../.claude/sandbox/probe-tmp.txt` was refused with the harness's own
    # "denied by your permission settings", while `docs/../docs/probe-tmp.txt`
    # was created — so the path is normalised and then matched, and `..` is not
    # what was rejected. Refusing every `..` here would therefore buy nothing
    # against the deny list and would refuse the second of those two, which is
    # innocent traffic. Raised by Copilot; the premise is what failed.
    joined = spelled if os.path.isabs(spelled) else os.path.join(cwd, spelled)
    lexical = os.path.normpath(os.path.abspath(joined))
    resolved = os.path.realpath(joined)

    # **Every anchor containing the target must agree, and the first form said
    # ANY.** One agreeing anchor was enough to admit the write, so a second
    # anchor could excuse what the first refused — and that is not hypothetical
    # arithmetic: with `cwd` taken as an anchor whatever it pointed at, a
    # session standing in a linked directory admitted the exact write this file
    # exists to refuse. Requiring agreement is what makes an extra anchor
    # incapable of widening the guard, which is the property `anchors` rests
    # its trust in `CLAUDE_PROJECT_DIR` on. Raised by Copilot.
    judged = False
    for spelled_root, real_root, folded in checkouts:
        if under(lexical, spelled_root, folded):
            base = spelled_root
        elif under(lexical, real_root, folded):
            base = real_root
        else:
            continue
        judged = True

        expected = os.path.normpath(
            os.path.join(real_root, os.path.relpath(lexical, base)))
        if not same(resolved, expected, folded):
            escaped = not under(resolved, real_root, folded)
            where = "outside the checkout" if escaped else "elsewhere in it"
            return (
                f"guard-edit-target: {spelled} resolves {where} — to "
                f"{resolved}. A permission rule matches the path as written, "
                "so an edit through a link lands where no deny has judged it. "
                "Write the file at its real path, or say why the link is "
                "there (#181, docs/harness-boundaries.md)."
            )

    # **A spelling no anchor recognises, naming a file inside one, is the
    # general form of three separate findings and it is refused here.** The
    # loop above judges a target it can place; everything else fell through to
    # the residual, and the residual is meant for a file that is genuinely
    # outside every checkout — not for one inside a checkout under a name the
    # anchors do not match. That difference is measurable: on a Windows runner
    # `GetShortPathNameW` shortens the whole prefix, so
    # `C:\Users\RUNNER~1\...\GUARD-~1\DOCUME~1\a.md` matched no anchor while
    # resolving squarely inside one, and the case written to pin the 8.3 alias
    # went red on CI having passed locally, where only the leaf was aliased.
    #
    # The same shape produced the case-folding finding and the Unicode one, and
    # both were closed by teaching the comparison a new equivalence. This
    # closes the class instead: whatever the spelling, if it RESOLVES into a
    # checkout that did not recognise it, the matcher judged a string that is
    # not this file and the write is refused. The residual is untouched — a
    # target resolving outside every anchor still falls through, which is what
    # keeps the session's own memory and scratch writes working.
    if not judged:
        for _, real_root, folded in checkouts:
            if under(resolved, real_root, folded):
                return (
                    f"guard-edit-target: {spelled} is not a spelling any "
                    f"checkout here recognises, yet it resolves to {resolved}, "
                    "inside one. A permission rule matches the string it is "
                    "given, so a name the rules cannot place is a write "
                    "nothing has judged (#181, docs/harness-boundaries.md)."
                )

    # Reached when every anchor containing the target agreed, or when the
    # target is outside every one of them — which is not this guard's subject,
    # and the module docstring argues why. A test pins that residual so the
    # next reader does not have to take the paragraph's word for it.
    return None


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
