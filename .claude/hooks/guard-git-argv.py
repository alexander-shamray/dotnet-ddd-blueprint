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
# **Judged by NAME and by PREFIX, not by set membership**, and both halves were
# holes. `--force-with-lease=feature` is not equal to `--force-with-lease`, so a
# set test admitted it; and git accepts any unambiguous abbreviation of a long
# option, so `--for` is a force push a full-spelling list never sees.
#
# `--all` and `--mirror` are here because they need no refspec at all: `--all`
# updates every branch the remote shares with this one, `main` included, and
# `--mirror` force-updates and deletes. A loop that inspects refspecs cannot see
# either — there is nothing for it to inspect. `--prune` deletes remote branches
# for the same reason.
PUSH_DANGEROUS_SHORT = {"-f", "-d"}
PUSH_DANGEROUS_LONG = (
    "force", "force-with-lease", "force-if-includes",
    "delete", "all", "mirror", "prune",
)
PROTECTED_BRANCHES = {"main"}

# Where one command ends and the next begins. A flag belongs to the `git` that
# precedes it, not to whatever ran before the `&&`.
SEPARATORS = {"&&", "||", ";", "|", "&", "(", ")", "{", "}", "\n"}

# **Flags whose next element is a VALUE, and a value is data rather than a flag.**
#
# This list exists because the guard refused its own commit. A commit body
# arguing about the run-a-command transport is one argv element after `-m`, and
# a substring check that does not know `-m` takes a value cannot tell prose
# *about* the transport from a command that *uses* it. The same shape reaches
# the flag checks: `git commit -m "--output is bad"` matches a check keyed on
# the element's prefix, because the element IS the message.
#
# One tool's "valid" is not the next tool's, and the gap is where a value
# crosses between them — git reads this element as a message, and a guard
# written for flags read it as a flag. Skip the value; judge the flags.
VALUE_FLAGS = {
    "-m", "--message", "-F", "--file", "-c", "--reedit-message",
    "-C", "--reuse-message", "--author", "--date", "--body", "--body-file",
    "--pathspec-from-file", "--grep", "--fixup", "--squash",
}

# The transport is only meaningful where git expects a REPOSITORY, so the check
# is scoped to the subcommands that take one. Judging it everywhere is the other
# half of what made an argument about it indistinguishable from a use of it —
# and scoping matters beyond commit messages, since any command may carry a path
# or a branch name that happens to contain the sequence.
REPOSITORY_SUBCOMMANDS = {
    "fetch", "clone", "pull", "push", "remote", "submodule", "ls-remote",
    "archive", "bundle",
}

# Git's own options, which sit BEFORE the subcommand. These take a separate
# value, so skipping one means skipping two elements — and getting that wrong is
# how `git -C <dir> push` reached the push guard with `-C` in the subcommand
# position and was waved through.
# Taken from git's own synopsis rather than from the options this file happened
# to hit — which is how `-C` was missed in the first place, and how
# `--attr-source` was missed in the fix for it. `--super-prefix` is gone: it is
# `unknown option` to the git on this host, so its presence was evidence the
# list had been written from memory.
#
# **This list still trails git's globals, and that is stated rather than
# implied** — a hardcoded skip-list is the same shape as the push deny-list this
# branch refused to extend, and pretending otherwise would be the exact mistake
# #23 is about. It is load-bearing only for the transport check now: the push
# check below no longer depends on it, because an unknown global must not be
# able to hide a force push the way `-C` and `--attr-source` both did.
GLOBAL_VALUE_FLAGS = {
    "-C", "-c", "--git-dir", "--work-tree", "--namespace", "--config-env",
    "--attr-source",
}


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


def after_global_options(segment):
    """`segment` from its SUBCOMMAND onward, with git's global options dropped.

    **`git -C <dir> push …` put `-C` where the subcommand goes**, so a check
    written as `segment[0] != "push"` never fired — a complete bypass of the
    push guard, found by writing a `git -C` command against the guard's own
    branch. Every global option is a way to say the same thing, which is the
    lesson #23 is about arriving one token earlier: the subcommand has to be
    *found*, not assumed to be first.
    """
    index = 0
    while index < len(segment) and segment[index].startswith("-"):
        if segment[index] in GLOBAL_VALUE_FLAGS:
            index += 2
        else:
            index += 1
    return segment[index:]


def dangerous_push_flag(element):
    """The long option `element` names, if it is a push flag that must refuse.

    Three normalisations, each of which was a hole:

      * `--flag=value` is split, so `--force-with-lease=feature` is judged as
        `--force-with-lease` rather than failing a set-membership test;
      * an ABBREVIATION is matched, because git accepts any unambiguous prefix
        of a long option — so `--for` is a force push that a list of full
        spellings never sees. A stem is dangerous when it is a prefix of a
        dangerous option, which is deliberately the same test git applies; and
      * the short forms are named separately, since `-f` is a prefix of nothing.

    `--follow-tags` is the case that shows the prefix test is the right way
    round: `"force".startswith("follow-tags")` is false, so it passes, while
    `--fo` is refused exactly as git would refuse it for being ambiguous.
    """
    if element in PUSH_DANGEROUS_SHORT:
        return element.lstrip("-")
    name = element.split("=", 1)[0]
    if not name.startswith("--"):
        return None
    stem = name[2:]
    if not stem:
        return None
    for full in PUSH_DANGEROUS_LONG:
        if full.startswith(stem):
            return full
    return None


def push_offence(segment):
    """The reason to refuse a `git push`, or None. `segment` is argv after `git`.

    Everything here is judged on the parsed refspec rather than on the string,
    for the reason #23 gives: a deny-list of spellings trails the grammar
    forever, and the grammar is not going to stop growing.
    """
    # **The push check does NOT ask where the subcommand is**, and that is the
    # fix for the second miss rather than the first. `-C` was closed by
    # `after_global_options`, and then `--attr-source HEAD push …` walked
    # through the same door, because the skip-list had been written from the
    # options this file happened to hit. A list that trails git's globals is the
    # deny-list shape #23 exists to refuse; making the check depend on it just
    # moves the enumeration.
    #
    # So `push` is LOCATED instead: the first bare `push` element, whatever
    # precedes it. An unknown global cannot hide it, because the guard no longer
    # needs to recognise the global at all.
    #
    # Safe against a ref that happens to be called `push` — `git log push`
    # slices to `["push"]`, which carries no dangerous flag and no refspec after
    # the remote, so nothing refuses it. The values of value-taking flags are
    # skipped first, so `git commit -m push` never reaches this.
    skip = False
    start = None
    for index, element in enumerate(segment):
        if skip:
            skip = False
            continue
        if element in VALUE_FLAGS:
            skip = True
            continue
        if element == "push":
            start = index
            break
    if start is None:
        return None
    segment = segment[start:]
    for element in segment[1:]:
        dangerous = dangerous_push_flag(element)
        if dangerous is not None:
            return (
                f"`git push --{dangerous}` rewrites or removes what is already "
                "published, in every spelling and abbreviation; that is a "
                "decision, not a step — run it yourself"
            )

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
        # **`shlex.split` does not tokenise shell operators**, and that was a
        # complete bypass: `git log --oneline&&git push origin +HEAD:main`
        # yields `--oneline&&git` as ONE element, so `git_segments` never starts
        # a second segment, the push check sees the subcommand `log`, and the
        # protected push is admitted. `git status;git push …` the same.
        #
        # Only whitespace-separated operators were ever recognised, which is the
        # `SEPARATORS` set below doing exactly what it says and nothing more.
        # `punctuation_chars=True` makes the lexer split `();<>|&` off as tokens
        # of their own, so an operator without spaces around it separates
        # commands here the way it does in the shell — while quoted content is
        # untouched, which is what keeps `git commit -m 'a && b'` one element.
        lexer = shlex.shlex(command, posix=True, punctuation_chars=True)
        lexer.whitespace_split = True
        tokens = list(lexer)
    except ValueError:
        # **Unparseable is not the same as hostile, and refusing it outright was
        # wrong.** The first version returned a refusal here on the reasoning
        # that the shell would fail on unbalanced quotes anyway. It does not:
        # `shlex` is a word splitter, not a shell, and it knows nothing about
        # HEREDOCS — so `git commit -F - <<'EOF'` with an apostrophe anywhere in
        # the body is unbalanced to `shlex` and perfectly valid to bash. That
        # refused an ordinary commit, which is the second time this guard fired
        # on innocent traffic, and a guard that does that is one somebody turns
        # off.
        #
        # So a parse failure DEGRADES to the check the settings file already
        # performs — a substring scan of the raw string — rather than to a
        # refusal or to nothing. That is never weaker than the status quo this
        # hook was added to improve on, and it keeps the fail-closed instinct
        # where it belongs: on what the guard can see, not on its own inability
        # to tokenise.
        for needle in FORBIDDEN_FLAGS + FORBIDDEN_SUBSTRINGS:
            if needle in command:
                return (
                    f"`{needle}` appears in a command this guard could not "
                    "tokenise; refusing on the raw string, which is the weaker "
                    "check the settings deny already performs."
                )
        return None

    for segment in git_segments(tokens):
        refusal = push_offence(segment)
        if refusal is not None:
            return refusal
        # The subcommand, found the same way the push check finds it — through
        # one helper rather than two loops that can disagree. The inline version
        # this replaces knew `-C` only because it happens to also be a
        # value-taking flag of `commit`, and would have read
        # `git --git-dir /x fetch …` as having the subcommand `/x`.
        stripped = after_global_options(segment)
        subcommand = stripped[0] if stripped else ""
        skip = False
        for element in segment:
            # A value belongs to the flag before it and is judged as data. This
            # has to run before every check below, or the guard reads a commit
            # message as an argument list.
            if skip:
                skip = False
                continue
            if element in VALUE_FLAGS:
                skip = True
                continue
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
            if subcommand not in REPOSITORY_SUBCOMMANDS:
                continue
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
