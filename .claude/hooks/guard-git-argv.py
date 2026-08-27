#!/usr/bin/env python3
"""Judge git on the argv the shell will execute, not on the string a caller types.

**This exists because a permission rule matches the typed STRING and the shell
executes an ARGV**, and the gap between those two is where #30 lived.
`.claude/settings.json` denies `Bash(git *--output*)`, which closes the naive
spelling and nothing more: the shell reassembles adjacent quoted fragments
before `exec`, so `--out''put=<path>` reaches git as `--output=<path>` while
never presenting the matcher with a contiguous `--output`. Measured, not
reasoned about — `printf '%s' --out''put=/tmp/x` prints `--output=/tmp/x`.
`CLAUDE.md` recorded that as an accepted limit and named the fix: "a helper that
spells its own flags, or a rule over the executed argv rather than the typed
string". This is the second.

It also closes one thing the rule system provably cannot express. `Bash(git
*ext::*)` passes settings validation and then matches NOTHING — the trailing
`:*` is consumed as the prefix-wildcard form — while `Bash(git *ext::**)` is
rejected at startup. So `ext::`, a git transport that RUNS its argument as a
command, has no expressible Bash deny. It has one here.

**The push half is an ALLOW-list, and that is the whole design (#23).** It began
as a deny-list of dangerous spellings and two review rounds took it apart, each
finding a form nobody had listed: `--force-with-lease=<ref>` (not equal to the
set entry), `--for` (git accepts unambiguous abbreviations), `-fv` (bundled
shorts), `--all` and `--branches` and `--mirror` and `--prune` (no refspec to
inspect), `refs/heads/*:refs/heads/*` (a wildcard destination that includes
`main` and equals nothing), `git push origin HEAD` and bare `git push origin`
(no destination named at all, so nothing can be shown NOT to be `main`).

That is the deny-list trailing the grammar — the exact failure #23 is about,
reappearing inside its fix in parser form. **So the question is inverted:** a
push is refused unless every part of it is recognised. One remote, one refspec
that names a destination, and options drawn from a fixed set. Everything else,
including every spelling nobody has thought of yet, is refused. The three
pushes `/ship` actually makes are pinned in the suite, so over-reach breaks
there rather than in the delivery chain.

**Three things the parser has to do before it can judge anything**, each found
by a reviewer after the previous fix looked complete:

  * **heredoc bodies are data.** `shlex` knows nothing about them, so a commit
    body was tokenised as arguments — refusing an honest commit that quoted a
    push, and only passing the first test because an apostrophe forced the
    fallback path. Stripped first.
  * **operators without spaces still separate commands.** `shlex.split` left
    `--oneline&&git` as one element, so `git log --oneline&&git push origin
    +HEAD:main` never started a second segment and the push was admitted.
    `punctuation_chars=True` fixes it and leaves quoted content alone.
  * **a command substitution is executed, not quoted away.** `git log "$(git
    push origin +HEAD:main)"` is one `shlex` token and two commands to the
    shell. Substitutions are extracted and judged in their own right.

**What a value-taking flag is depends on the SUBCOMMAND**, and defaulting the
other way was a hole: `-m` takes a value for `commit` and takes none for `log`,
so a global skip-list let `git log -m --out''put=<path> --format=%B` walk the
skipped element straight past the check — #30, reopened by its own fix. The map
below is consulted per subcommand and **skips nothing by default**, because the
failure directions are not symmetric: not skipping costs a false positive, and
skipping wrongly costs a bypass.

**The residuals, stated rather than left to be found.** `shlex` resolves
quoting and command substitution is handled, but not *expansion*: a flag
assembled at run time — `F=--output=x; git log $F` — arrives as the token `$F`
and is not seen. Closing that needs the argv after expansion, which no hook is
given. And the value-flag map trails git's options the way any list does; it is
load-bearing only for false positives now, never for a bypass.

Protocol: PreToolUse, matcher `Bash`. Exit 0 and print nothing to allow; print
the deny JSON to refuse. Exit 2 would also block, but the JSON form carries a
reason the caller can read, and a guard that refuses without saying why is one
that gets worked around rather than fixed.
"""

import json
import re
import shlex
import sys

# Flags that write or execute rather than inspect. Matched on a PREFIX, so
# `--exec-path=<dir>` — a directory of binaries for git to run — is the same act
# as `--exec`; an earlier form matched exactly-or-`=` and admitted it, which the
# crude substring deny had been catching all along.
FORBIDDEN_FLAGS = ("--output", "--upload-pack", "--receive-pack", "--exec")

# Judged against a whole element, and only on a subcommand that takes a
# repository — a branch name, a path or a commit body may carry the sequence
# without using it as a transport.
FORBIDDEN_SUBSTRINGS = ("ext::",)
REPOSITORY_SUBCOMMANDS = {
    "fetch", "clone", "pull", "push", "remote", "submodule", "ls-remote",
    "archive", "bundle",
}

# Git's own options, which sit before the subcommand. Taken from git's synopsis
# rather than from the options this file happened to hit — which is how `-C` was
# missed, and then `--attr-source` in the fix for it. **This list still trails
# git's globals and that is stated rather than implied**; it is load-bearing
# only for locating a subcommand, never for the push check, which no longer asks
# where the subcommand is.
GLOBAL_VALUE_FLAGS = {
    "-C", "-c", "--git-dir", "--work-tree", "--namespace", "--config-env",
    "--attr-source",
}

# Per subcommand, because arity is not a property of a flag name: `-m` is a
# message for `commit` and "show merge diffs" for `log`. Absent an entry, NOTHING
# is skipped — a false positive is cheap and a bypass is not.
VALUE_FLAGS_BY_SUBCOMMAND = {
    "commit": {"-m", "--message", "-F", "--file", "-C", "--reuse-message",
               "-c", "--reedit-message", "--author", "--date", "--squash",
               "--fixup", "--pathspec-from-file"},
    "tag": {"-m", "--message", "-F", "--file", "-u", "--local-user"},
    "merge": {"-m", "--message", "-F", "--file", "-S", "--strategy"},
    "stash": {"-m", "--message"},
    "notes": {"-m", "--message", "-F", "--file"},
    "revert": {"-m", "--mainline", "-S"},
    "cherry-pick": {"-m", "--mainline", "-S"},
    "branch": {"-u", "--set-upstream-to", "--contains", "--sort"},
}

SEPARATORS = {"&&", "||", ";", "|", "&", "(", ")", "{", "}", "\n"}

# ---- the push allow-list ---------------------------------------------------

# Options a push may carry. Anything else — including a spelling git invented
# last week, an abbreviation, or a bundle like `-fv` — is refused rather than
# checked against a list of what is dangerous.
PUSH_ALLOWED_FLAGS = {
    "-u", "--set-upstream", "-q", "--quiet", "-v", "--verbose",
    "--porcelain", "--progress", "--no-progress", "--atomic", "--no-verify",
    "--follow-tags", "-n", "--dry-run",
}

# A ref this guard is willing to read: no `*`, no `+`, no `:` beyond the one
# separator, nothing that could be a pattern or an option.
SAFE_REF = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._/-]*$")
SAFE_REMOTE = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]*$")

# Sources that name no destination of their own. `git push origin HEAD` updates
# whatever branch you are standing on — `main`, if you are on `main` — and a
# hook is not given the repository state to find out which.
UNRESOLVABLE_SOURCES = {"HEAD", "@", "HEAD~", "@{u}", "@{upstream}"}

PROTECTED_BRANCHES = {"main"}

# Heredoc introducers. The body between the introducer and its delimiter is data
# the shell hands to a command, not a command line.
HEREDOC = re.compile(r"<<-?\s*(['\"]?)([A-Za-z_][A-Za-z0-9_]*)\1")


def strip_heredocs(command):
    """`command` with every heredoc BODY removed, delimiters included.

    A heredoc body is an argument, and parsing it as a command line is how the
    guard came to refuse an honest commit that quoted a push. The introducer is
    left in place so the rest of the line still tokenises.
    """
    out = []
    rest = command
    while True:
        match = HEREDOC.search(rest)
        if match is None:
            out.append(rest)
            break
        out.append(rest[:match.end()])
        after = rest[match.end():]
        delimiter = match.group(2)
        closing = re.search(rf"^\s*{re.escape(delimiter)}\s*$", after, re.MULTILINE)
        if closing is None:
            # Unterminated: the whole tail is body. Dropping it is right — an
            # unterminated heredoc is not a command line either.
            break
        rest = after[closing.end():]
    return "".join(out)


def substitutions(command):
    """Every `$(...)` and backtick body in `command`, innermost included.

    These are COMMANDS the shell executes, and `shlex` hands them back as one
    quoted token — so `git log "$(git push origin +HEAD:main)"` contains no
    standalone `git` for the segment scan to find. Extracted and judged in
    their own right.
    """
    found = []
    index = 0
    while index < len(command):
        if command.startswith("$(", index):
            depth, cursor = 1, index + 2
            while cursor < len(command) and depth:
                if command.startswith("$(", cursor):
                    depth += 1
                    cursor += 2
                    continue
                if command[cursor] == ")":
                    depth -= 1
                elif command[cursor] == "(":
                    depth += 1
                cursor += 1
            if not depth:
                found.append(command[index + 2:cursor - 1])
            index = cursor
            continue
        if command[index] == "`":
            end = command.find("`", index + 1)
            if end == -1:
                break
            found.append(command[index + 1:end])
            index = end + 1
            continue
        index += 1
    return found


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
    """`segment` from its subcommand onward, with git's global options dropped."""
    index = 0
    while index < len(segment) and segment[index].startswith("-"):
        index += 2 if segment[index] in GLOBAL_VALUE_FLAGS else 1
    return segment[index:]


def subcommand_of(segment):
    stripped = after_global_options(segment)
    return stripped[0] if stripped else ""


def push_offence(segment):
    """The reason to refuse a `git push`, or None — by ALLOW-list.

    `push` is LOCATED rather than assumed to be first, so no global option,
    known or not, can hide it: that was `-C`, and then `--attr-source` in the
    fix for `-C`.
    """
    # `push` is the subcommand when everything before it is either an option or
    # an option's value — and a value is recognised STRUCTURALLY, as a non-flag
    # immediately preceded by a flag, rather than by consulting a list of
    # value-taking globals. That is what makes an unknown global harmless:
    # `git --attr-source HEAD push` and `git --some-future-global X push` both
    # resolve, without this file knowing either flag.
    #
    # It also keeps `git log push` — a ref that happens to be called `push` —
    # out of the push checks, because `log` is a non-flag that no flag precedes,
    # so `log` is the subcommand and `push` is one of its arguments. Refusing
    # that was the one false positive the allow-list introduced, and trading it
    # away would have been the wrong direction: a guard that fires on innocent
    # traffic is one somebody turns off.
    start = None
    for index, element in enumerate(segment):
        if element.startswith("-"):
            continue
        if element == "push":
            start = index
            break
        if index == 0 or not segment[index - 1].startswith("-"):
            break  # this is the subcommand, and it is not `push`
    if start is None:
        return None
    rest = segment[start + 1:]

    for element in rest:
        if element.startswith("-") and element not in PUSH_ALLOWED_FLAGS:
            return (
                f"`git push {element}` is not one of the options this guard "
                "recognises. A push is admitted only when every part of it is "
                "known — one remote, one refspec naming a destination, and "
                "options from a fixed set. Refusing what is unrecognised is "
                "what stops the next spelling nobody listed."
            )

    positional = [a for a in rest if not a.startswith("-")]
    if len(positional) != 2:
        return (
            "a push must name a remote and exactly one refspec. "
            "`git push origin` and `git push origin HEAD` name no destination, "
            "so neither can be shown not to be a protected branch — a hook is "
            "given no repository state to resolve them against."
        )

    remote, refspec = positional
    if not SAFE_REMOTE.match(remote):
        return f"`{remote}` is not a plain remote name"

    if refspec.startswith("+"):
        return "a `+` refspec is a force push — the spelling that carries no `--force`"
    if ":" in refspec:
        source, _, destination = refspec.partition(":")
        if not source:
            return "a `:branch` refspec deletes the remote branch"
    else:
        source, destination = refspec, refspec
    if destination.startswith("refs/heads/"):
        destination = destination[len("refs/heads/"):]
    if source in UNRESOLVABLE_SOURCES and destination == source:
        return (
            f"`{source}` names no destination of its own; it updates whatever "
            "branch you are standing on, which a hook cannot resolve"
        )
    if not SAFE_REF.match(destination):
        return (
            f"`{destination}` is not a plain branch name. A wildcard or pattern "
            "destination can include a protected branch while equalling none — "
            "`refs/heads/*:refs/heads/*` is the case that made this an "
            "allow-list."
        )
    if destination in PROTECTED_BRANCHES:
        return (
            f"pushing to `{destination}` is a decision, not a step, in every "
            "spelling of the refspec"
        )
    return None


def offence(command):
    """The reason to refuse `command`, or None to allow it."""
    for inner in substitutions(command):
        refusal = offence(inner)
        if refusal is not None:
            return f"inside a command substitution: {refusal}"

    try:
        lexer = shlex.shlex(strip_heredocs(command), posix=True,
                            punctuation_chars=True)
        lexer.whitespace_split = True
        tokens = list(lexer)
    except ValueError:
        # **Unparseable is not hostile.** The first version refused anything it
        # could not tokenise, reasoning that bash would fail too — false about
        # the parser in use, and it refused an ordinary commit. A parse failure
        # DEGRADES to the substring scan the settings deny already performs:
        # never weaker than the status quo, never a silent pass.
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

        subcommand = subcommand_of(segment)
        value_flags = VALUE_FLAGS_BY_SUBCOMMAND.get(subcommand, frozenset())
        skip = False
        for element in segment:
            if skip:
                skip = False
                continue
            if element in value_flags:
                skip = True
                continue
            for flag in FORBIDDEN_FLAGS:
                if element.startswith(flag):
                    return (
                        f"`git ... {flag}` is refused: it writes or executes "
                        "rather than inspects, and the settings deny it matches "
                        "only the unquoted spelling. This hook compares the "
                        "resolved argv."
                    )
            if subcommand not in REPOSITORY_SUBCOMMANDS:
                continue
            for substring in FORBIDDEN_SUBSTRINGS:
                if substring in element:
                    return (
                        f"`{substring}` is a git transport that runs its "
                        "argument as a command, and no Bash permission rule can "
                        "express it."
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
