#!/usr/bin/env python3
"""Refuse the git flags that turn an inspection command into a write primitive.

**This exists because a permission rule matches the typed STRING and the shell
executes an ARGV**, and the gap between those two is where #30 lived.
`.claude/settings.json` denies `Bash(git *--output*)`, which closes the naive
spelling and nothing more: the shell reassembles adjacent quoted fragments
before `exec`, so `--out''put=<path>` reaches git as `--output=<path>` while
never presenting the matcher with a contiguous `--output`. `CLAUDE.md` recorded
that as an accepted limit and named the fix — "a helper that spells its own
flags, or a rule over the executed argv rather than the typed string".

This is the second of those. `shlex.split` performs the same quote removal the
shell does, so the fragments are rejoined here before anything is compared, and
the substring dodge stops working. Nothing about the flag list is new; what is
new is that it is applied to what git will actually receive.

**It also closes one thing the rule system provably cannot express.**
`Bash(git *ext::*)` passes settings validation and then matches NOTHING — the
trailing `:*` is consumed as the prefix-wildcard form and the literal degrades
to `git *ext:` — while `Bash(git *ext::**)` is rejected at startup with "The
`:*` pattern must be at the end." So `ext::`, a git transport that RUNS its
argument as a command, has no expressible Bash deny. It has one here.

Scope is deliberately narrow: a flag is judged only inside the `git` invocation
it belongs to. Blocking every `--output` token anywhere would refuse
`dotnet publish --output`, which is an ordinary command with no such history,
and a guard that fires on innocent traffic is one somebody turns off.

**The residual, stated rather than left to be found.** `shlex` resolves quoting
and not expansion, so a flag assembled at run time — `F=--output=x; git log $F`
— arrives here as the token `$F` and is not seen. Closing that needs the argv
after expansion, which no hook is given. The bound is: this refuses every
spelling a caller can type literally, quoted however they like, and does not
refuse one the shell computes.

Protocol: PreToolUse, matcher `Bash`. Exit 0 and print nothing to allow; print
the deny JSON to refuse. Exit 2 would also block, but the JSON form carries a
reason the caller can read, and a guard that refuses without saying why is one
that gets worked around rather than fixed.
"""

import json
import shlex
import sys

# Each entry is matched against a whole argv element and against its `=`-prefixed
# form, so `--output x` and `--output=x` are one rule rather than two.
#
# The first is the write primitive #30 reported: with `--format=` choosing the
# bytes, `git log --output=<path>` is an arbitrary-content write to an arbitrary
# path, and it reads as inspection. The other three run a command of the
# caller's choosing on this host.
FORBIDDEN_FLAGS = (
    "--output",
    "--upload-pack",
    "--receive-pack",
    "--exec",
)

# Substrings judged against a whole element rather than the command line. `ext::`
# is git's run-a-command transport; `Bash(...)` cannot express it at all.
FORBIDDEN_SUBSTRINGS = ("ext::",)

# `git push` is #23, and it is the same defect in a different grammar. The
# settings file pairs two broad allows — `Bash(git push origin:*)` and the `-u`
# form — with a deny-list of exact spellings, so every invocation the list did
# not anticipate is auto-approved: `git push origin +HEAD:main` is a force push
# to main carrying neither `--force` nor the literal `origin main`;
# `git push origin :branch` is a remote delete the `--delete` deny never sees;
# `git push origin HEAD:refs/heads/main` is the fully-qualified spelling.
#
# **Deny-list enumeration will always trail git's refspec grammar**, which is
# the issue's own conclusion. So this parses the refspec instead of matching it:
# what is refused is a *destination* of main, a force in any spelling, and a
# delete in any spelling — three properties, rather than the six spellings that
# happened to be noticed.
PUSH_FORCE_FLAGS = {"-f", "--force", "--force-with-lease", "--force-if-includes"}
PUSH_DELETE_FLAGS = {"-d", "--delete"}
PROTECTED_BRANCHES = {"main"}

# Where one command ends and the next begins. A flag belongs to the `git` that
# precedes it, not to whatever ran before the `&&`.
SEPARATORS = {"&&", "||", ";", "|", "&", "(", ")", "{", "}", "\n"}


def git_segments(tokens):
    """Yield the argv slice of every `git` invocation in a compound command."""
    for index, token in enumerate(tokens):
        if token != "git" and not token.endswith("/git"):
            continue
        segment = []
        for following in tokens[index + 1:]:
            if following in SEPARATORS:
                break
            segment.append(following)
        yield segment


def push_offence(segment):
    """The reason to refuse a `git push`, or None. `segment` is argv after `git`.

    Everything here is judged on the parsed refspec rather than on the string,
    for the reason #23 gives: a deny-list of spellings trails the grammar
    forever, and the grammar is not going to stop growing.
    """
    if not segment or segment[0] != "push":
        return None
    flags = {a for a in segment[1:] if a.startswith("-")}
    if flags & PUSH_FORCE_FLAGS:
        return "a force push is a decision, not a step; run it yourself"
    if flags & PUSH_DELETE_FLAGS:
        return "deleting a remote branch is a decision, not a step; run it yourself"

    positional = [a for a in segment[1:] if not a.startswith("-")]
    # positional[0] is the remote; everything after it is a refspec.
    for refspec in positional[1:]:
        if refspec.startswith("+"):
            return (
                "a `+` refspec is a force push — the spelling that carries no "
                "`--force` and that a prefix deny cannot see"
            )
        if refspec.startswith(":"):
            return (
                "a `:branch` refspec deletes the remote branch — the spelling "
                "that carries no `--delete`"
            )
        destination = refspec.split(":")[-1]
        for prefix in ("refs/heads/",):
            if destination.startswith(prefix):
                destination = destination[len(prefix):]
        if destination in PROTECTED_BRANCHES:
            return (
                f"pushing to `{destination}` is a decision, not a step, in every "
                "spelling of the refspec"
            )
    return None


def offence(command):
    """The reason to refuse `command`, or None to allow it."""
    try:
        tokens = shlex.split(command, posix=True)
    except ValueError as error:
        # Unbalanced quoting. The shell would fail on this too, so refusing
        # costs nothing — and guessing at what it "meant" is how a guard that
        # cannot parse its input ends up admitting what it cannot see.
        return f"could not parse the command as argv ({error}); refusing to guess"

    for segment in git_segments(tokens):
        refusal = push_offence(segment)
        if refusal is not None:
            return refusal
        for element in segment:
            for flag in FORBIDDEN_FLAGS:
                # A PREFIX match, not an exact one plus its `=` form, and the
                # difference is `--exec-path=<dir>` — which points git at
                # another directory of binaries to run and is therefore the
                # same act as `--exec`. The settings deny `Bash(git *--exec*)`
                # is a substring match and already caught it; a replacement
                # narrower than the rule it replaces is a regression wearing a
                # fix's clothes.
                if element.startswith(flag):
                    return (
                        f"`git ... {flag}` is refused: it writes or executes rather "
                        "than inspects, and the settings deny it matches only the "
                        "unquoted spelling. This hook compares the resolved argv."
                    )
            for substring in FORBIDDEN_SUBSTRINGS:
                if substring in element:
                    return (
                        f"`{substring}` is a git transport that runs its argument "
                        "as a command, and no Bash permission rule can express it."
                    )
    return None


def main():
    try:
        event = json.load(sys.stdin)
    except (json.JSONDecodeError, ValueError):
        # A hook that cannot read its own input has established nothing. Say so
        # and allow: refusing every Bash call on a malformed event would take
        # the session down for a defect in this file.
        print("guard-git-argv: unreadable hook event; not judging", file=sys.stderr)
        return 0

    if event.get("tool_name") != "Bash":
        return 0

    command = (event.get("tool_input") or {}).get("command")
    if not isinstance(command, str):
        return 0

    reason = offence(command)
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
