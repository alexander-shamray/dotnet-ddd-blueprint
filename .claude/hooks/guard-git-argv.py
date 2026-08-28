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

# `git -c <key>=<value>` sets configuration for one invocation, and a long list
# of config keys are EXECUTED by git: `alias.*`, `core.pager`, `core.editor`,
# `core.sshCommand`, `core.hooksPath`, `diff.external`, `diff.*.textconv`,
# `filter.*.clean`, `credential.helper`, `sequence.editor`, `gpg.program`,
# `uploadpack.packObjectsHook`. Measured, not reasoned about:
# `git -c "alias.x=!echo PWNED" x` prints PWNED.
#
# **Enumerating the executing keys is the deny-list this repository has refused
# twice**, and git's list grows on git's schedule rather than on ours. So the
# OPTION is refused instead of its values being judged — nothing in this
# repository passes `-c` or `--config-env` to git, which is what makes that
# affordable. If a caller ever needs one, the honest change is an allow-list of
# keys, not a list of the dangerous ones.
CONFIG_OPTIONS = ("-c", "--config-env")
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


def shell_positions(command):
    """Walk `command`, yielding `(index, in_quotes, in_comment)` per character.

    **One scanner, because both callers were defeated by the same thing.** A
    regex search for `<<` found a heredoc opener inside a COMMENT, so
    `git status # <<EOF` swallowed the real command on the next line before it
    could be judged; and a paren counter that did not know about quotes let
    `git log "$(printf ')'; git push origin +HEAD:main)"` close early, hiding
    the push in the outer token. Both raised in review, both verified allowed.

    `in_quotes` is true inside `'…'` and `"…"` alike. Comments start at an
    unquoted `#` that begins a word and end at the newline — which is bash's
    rule, and the reason `git log --grep=#x` is not a comment.
    """
    single = double = comment = False
    index = 0
    while index < len(command):
        char = command[index]
        if comment:
            if char == "\n":
                comment = False
            else:
                yield index, False, True
                index += 1
                continue
        if not comment:
            if single:
                if char == "'":
                    single = False
            elif double:
                if char == "\\" and index + 1 < len(command):
                    # **Both characters, because a consumer rebuilds text from
                    # these positions.** Yielding only the backslash made
                    # `strip_comments` DELETE the escaped character, so
                    # `git log "$(printf \); git push …)"` lost its `)` and
                    # changed shape on its way through the guard. A scanner that
                    # silently edits its input is worse than one that misreads
                    # it, because every later stage inherits the edit.
                    yield index, True, False
                    yield index + 1, True, False
                    index += 2
                    continue
                if char == '"':
                    double = False
            elif char == "'":
                single = True
            elif char == '"':
                double = True
            elif char == "#" and (index == 0 or command[index - 1] in " \t\n;&|("):
                comment = True
                yield index, False, True
                index += 1
                continue
        yield index, single or double, comment
        index += 1


def heredoc_spans(command):
    """Every heredoc body in `command`, as `(start, end, expands)`.

    `start` is just past the introducer and `end` just past the closing
    delimiter line, so `command[start:end]` is everything the shell hands over
    as data rather than reading as a command line.

    **`expands` is the half this used to throw away**, and throwing it away was
    a bypass rather than an imprecision. `<<'EOF'` and `<<"EOF"` hand the body
    over verbatim; a bare `<<EOF` performs substitution and parameter expansion
    on it first. A guard that treats both as inert misses a live
    `$(git push origin +HEAD:main)` in the second, and a guard that treats both
    as executable refuses an honest commit quoting one in the first. Only the
    delimiter's quoting tells them apart, and `HEREDOC` has always captured it.

    **An opener is only an opener in executable position.** A `<<EOF` inside a
    comment or inside quotes is text, and treating it as an operator let
    `git status # <<EOF` delete the command on the following line — the guard
    removing the very thing it exists to read.
    """
    openers = []
    for index, in_quotes, in_comment in shell_positions(command):
        if in_quotes or in_comment:
            continue
        if command.startswith("<<", index) and (not openers or index >= openers[-1][0]):
            match = HEREDOC.match(command, index)
            if match:
                openers.append((match.end(), match.group(1), match.group(2)))

    spans, pending = [], 0
    for intro_end, quote, delimiter in openers:
        # An introducer sitting inside an earlier body is body text, not an
        # opener. Containment, not "before the cursor" — two heredocs stacked on
        # ONE line both introduce before either body starts, so an ordering test
        # discards the second.
        if any(start <= intro_end < end for start, end, _ in spans):
            continue

        # **A body begins on the NEXT LINE, and taking it to begin at the
        # introducer was a third admitted force push.** Everything between the
        # introducer and that newline is still command line, so
        # `cat <<'A' ; git push origin +HEAD:main` had the push swallowed as
        # data and the hook returned nothing. Verified under bash: it runs.
        newline = command.find("\n", intro_end)
        if newline == -1:
            # An introducer with no line after it opens no body at all.
            continue

        # Stacked bodies queue: the second starts where the first terminated,
        # which is past its own line break.
        start = max(newline + 1, pending)
        closing = re.search(
            rf"^\s*{re.escape(delimiter)}\s*$", command[start:], re.MULTILINE)
        if closing is None:
            # Unterminated: the whole tail is body. Treating it as one is right
            # — an unterminated heredoc is not a command line either.
            spans.append((start, len(command), not quote))
            break
        pending = start + closing.end()
        spans.append((start, pending, not quote))
    return spans


def strip_heredocs(command):
    """`command` with every heredoc BODY removed, delimiters included.

    A heredoc body is an argument, and parsing it as a command line is how the
    guard came to refuse an honest commit that quoted a push. The introducer is
    left in place so the rest of the line still tokenises.
    """
    out, cursor = [], 0
    for start, end, _expands in heredoc_spans(command):
        out.append(command[cursor:start])
        cursor = end
    out.append(command[cursor:])
    return "".join(out)


def strip_comments(command):
    """`command` with every shell COMMENT removed, newlines kept.

    **bash's rule, not `shlex`'s, and the difference is a force push.**
    `shlex.shlex` sets `commenters = "#"` and honours it at any character
    position, so `git log --grep=#x ; git push origin +HEAD:main` tokenised to
    three tokens and the push vanished with the rest of the line — admitted, and
    verified running under bash, which starts a comment only where `#` begins a
    word. The lexer's comment handling is switched off in `offence` and this
    runs instead, over the scanner that already implements that rule for
    heredoc openers.
    """
    return "".join(
        command[index]
        for index, _in_quotes, in_comment in shell_positions(command)
        if not in_comment
    )


def expandable_regions(command):
    """Every part of `command` the shell would expand, as `(text, quotes)`.

    Substitution extraction used to run over the raw string with a quote tracker
    of its own and no notion of heredocs or comments, which made it disagree
    with the rest of the guard in both directions at once — verified, both ways:

    | Command | bash | the guard was |
    |---|---|---|
    | `git commit -F - <<'EOF'` … `$(git push origin +HEAD:main)` | does not expand | refusing |
    | `git commit -F - <<EOF` … `don't $(git push origin +HEAD:main)` | expands | admitting |

    The second is the one that matters: an apostrophe in the body is a quote to
    a raw scanner and a character to bash, so the live substitution was skipped
    and the push ran. `quotes` is what carries that — inside a heredoc body
    there are no quotes to honour, only expansions to perform.

    The command line itself arrives with bodies and comments already gone, so a
    `$(…)` the shell would never reach cannot be judged as though it would.
    """
    line, regions, cursor = [], [], 0
    for start, end, expands in heredoc_spans(command):
        line.append(command[cursor:start])
        if expands:
            regions.append((command[start:end], False))
        cursor = end
    line.append(command[cursor:])
    return [(strip_comments("".join(line)), True)] + regions


def substitutions(command, quotes=True):
    """Every `$(...)` and backtick body in `command`, innermost included.

    These are COMMANDS the shell executes, and `shlex` hands them back as one
    quoted token — so `git log "$(git push origin +HEAD:main)"` contains no
    standalone `git` for the segment scan to find. Extracted and judged in
    their own right.

    `quotes` is false for a heredoc body, where `'` is an ordinary character
    rather than a quote. See `expandable_regions` for what that cost.
    """
    found = []
    index = 0
    # Single-quote state only: `$(` is live inside DOUBLE quotes, which is the
    # whole shape of the bypass — `git log "$(git push …)"`.
    in_single = False
    while index < len(command):
        char = command[index]
        if in_single:
            if char == "'":
                in_single = False
            index += 1
            continue
        if char == "'" and quotes:
            in_single = True
            index += 1
            continue
        if char == "\\":
            # `\$(x)` is a literal `$(` to bash, on the command line and in an
            # unquoted heredoc body alike. Skipping the escaped character keeps
            # the guard off a substitution the shell will never perform.
            index += 2
            continue
        if command.startswith("$(", index):
            end = _closing_paren(command, index + 2)
            if end is None:
                break
            found.append(command[index + 2:end])
            index = end + 1
            continue
        if char == "`":
            end = command.find("`", index + 1)
            if end == -1:
                break
            found.append(command[index + 1:end])
            index = end + 1
            continue
        index += 1
    return found


def _closing_paren(command, start):
    """Index of the `)` closing a substitution opened before `start`, or None.

    **Quotes are tracked while balancing**, because a paren counter that reads
    raw characters closes early on a quoted one:
    `git log "$(printf ')'; git push origin +HEAD:main)"` ended extraction at
    the `)` inside `'…'`, leaving the push hidden in the outer token. Raised in
    review; verified allowed.
    """
    depth, index = 1, start
    single = double = False
    while index < len(command):
        char = command[index]
        if single:
            if char == "'":
                single = False
        elif double:
            if char == "\\" and index + 1 < len(command):
                index += 2
                continue
            if char == '"':
                double = False
        elif char == "\\" and index + 1 < len(command):
            # An unquoted `\)` is a literal paren to bash, so counting it closed
            # the substitution early and hid the rest of it in the outer token:
            # `git log "$(printf \); git push origin +HEAD:main)"`. The escape
            # was handled inside double quotes and nowhere else. Raised in
            # review; the bash behaviour measured — `printf` receives the paren
            # and the push runs.
            index += 2
            continue
        elif char == "'":
            single = True
        elif char == '"':
            double = True
        elif command.startswith("$(", index):
            depth += 1
            index += 2
            continue
        elif char == "(":
            depth += 1
        elif char == ")":
            depth -= 1
            if not depth:
                return index
        index += 1
    return None


# A shell invoked with `-c` runs its argument as a command line, and `eval` runs
# the concatenation of its own. Both hand the guard a command it must read as
# one rather than as data.
EVALUATORS = {"bash", "sh", "dash", "zsh", "ksh"}

# `-c`, and the bundles that carry it — `bash -xc <script>`. A long option is
# never the script introducer, so `--` forms are left alone.
SCRIPT_FLAG = re.compile(r"^-[A-Za-z]*c$")


# Windows resolves `git.exe`, `GIT.EXE` and `C:/Git/bin/git.exe` to one
# program, and this repository is developed on Windows.
EXECUTABLE_SUFFIXES = (".exe", ".cmd", ".bat", ".com")


def program_name(token):
    """The program `token` names, normalised for comparison.

    **The segment scan matched the literal `git` and a `/git` suffix**, so
    `git.exe push origin +HEAD:main` walked straight past it — and so did
    `bash.exe -c`. Verified on this host: `git.exe --version` and
    `bash.exe -c` both run. Found by probing the shapes adjacent to a fix,
    which is also how the platform came up: every case in this file had been
    written in POSIX spelling on a machine that answers to both.

    Lower-cased because Windows paths are case-insensitive. On a system where
    they are not, `GIT` names nothing and refusing it costs nothing.
    """
    name = re.split(r"[\\/]", token)[-1].lower()
    for suffix in EXECUTABLE_SUFFIXES:
        if name.endswith(suffix):
            return name[: -len(suffix)]
    return name


def _argv_after(tokens, index):
    """The argv slice following `tokens[index]`, up to the next separator."""
    argv = []
    for following in tokens[index + 1:]:
        if following in SEPARATORS:
            break
        argv.append(following)
    return argv


def evaluated_scripts(tokens):
    """Every token a shell evaluator in `tokens` will execute as a command.

    **`shlex` hands a quoted script back as one data token**, exactly as it does
    a command substitution — so `git log "$(bash -c 'git push origin
    +HEAD:main')"` reached the inner pass as `bash`, `-c` and one opaque string,
    the segment scan found no `git`, and the push ran. Raised in review;
    verified allowed, and the bash behaviour measured with a `git` shim.

    **The bound: a script this hook can READ.** `bash script.sh` runs a file,
    and a hook is handed an argv rather than a filesystem — that is outside what
    any argv guard can see, and it is the same shape as the parameter-expansion
    residual rather than a new one.
    """
    for index, token in enumerate(tokens):
        name = program_name(token)
        if name in EVALUATORS:
            argv = _argv_after(tokens, index)
            for position, element in enumerate(argv):
                if SCRIPT_FLAG.match(element) and position + 1 < len(argv):
                    yield argv[position + 1]
                    break
        elif name == "eval":
            argv = _argv_after(tokens, index)
            if argv:
                yield " ".join(argv)


def git_segments(tokens):
    """Yield the argv slice of every `git` invocation in a compound command."""
    for index, token in enumerate(tokens):
        if program_name(token) != "git":
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


def global_options(segment):
    """`segment`'s leading global options — everything before the subcommand.

    The position is the whole point: `-c` before the subcommand is git's
    configuration option, and `-c` after `commit` is "reuse this commit's
    message". Refusing the second would break an ordinary commit, so the two
    are told apart the way git tells them apart — by where they stand.
    """
    stripped = after_global_options(segment)
    if not stripped:
        return segment
    return segment[:len(segment) - len(stripped)]


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


# Substitutions and evaluators both recurse, and a crafted nest of either would
# otherwise reach Python's own limit — where the hook dies with a traceback
# rather than a verdict, which is the one direction a guard must not fail in.
MAX_NESTING = 24


def offence(command, depth=0):
    """The reason to refuse `command`, or None to allow it."""
    if depth > MAX_NESTING:
        return (
            "this command nests shells or substitutions more deeply than the "
            "guard will follow; refusing rather than reading part of it."
        )

    for text, quotes in expandable_regions(command):
        for inner in substitutions(text, quotes=quotes):
            refusal = offence(inner, depth + 1)
            if refusal is not None:
                return f"inside a command substitution: {refusal}"

    # Stripped once, and used by BOTH paths below. The fallback used to scan the
    # raw `command`, which put the heredoc false positive straight back: a body
    # that mentions a forbidden flag would be refused on the raw string the
    # moment anything else in the line failed to tokenise. A body is data on
    # every path, not only on the one that parses — and so is a comment, which
    # is why `strip_comments` runs here rather than being left to the lexer.
    stripped = strip_comments(strip_heredocs(command))
    try:
        lexer = shlex.shlex(stripped, posix=True, punctuation_chars=True)
        # Comments are already gone, and `shlex` would take a second, wider view
        # of them: its `commenters` fires mid-word, where bash's fires only at
        # the start of one. Left on, `--grep=#x ; git push origin +HEAD:main`
        # lost the push to the lexer.
        lexer.commenters = ""
        lexer.whitespace_split = True
        tokens = list(lexer)
    except ValueError:
        # **Unparseable is not hostile.** The first version refused anything it
        # could not tokenise, reasoning that bash would fail too — false about
        # the parser in use, and it refused an ordinary commit. A parse failure
        # DEGRADES to the substring scan the settings deny already performs:
        # never weaker than the status quo, never a silent pass.
        for needle in FORBIDDEN_FLAGS + FORBIDDEN_SUBSTRINGS:
            if needle in stripped:
                return (
                    f"`{needle}` appears in a command this guard could not "
                    "tokenise; refusing on the raw string, which is the weaker "
                    "check the settings deny already performs."
                )
        return None

    for script in evaluated_scripts(tokens):
        refusal = offence(script, depth + 1)
        if refusal is not None:
            return f"inside a shell evaluator: {refusal}"

    for segment in git_segments(tokens):
        refusal = push_offence(segment)
        if refusal is not None:
            return refusal

        for element in global_options(segment):
            if element in CONFIG_OPTIONS or any(
                    element.startswith(option + "=") for option in CONFIG_OPTIONS):
                return (
                    "`git -c` / `--config-env` sets configuration for one "
                    "invocation, and git EXECUTES several config keys — "
                    "`alias.*`, `core.pager`, `core.sshCommand`, "
                    "`core.hooksPath` and more. Nothing here passes one, so "
                    "the option is refused rather than its value guessed at."
                )

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
