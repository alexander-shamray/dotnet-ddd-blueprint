#!/usr/bin/env python3
"""Judge git on the argv the shell will execute, not on the string a caller types.

**This exists because a permission rule matches the typed STRING and the shell
executes an ARGV**, and the gap between those two is where #30 lived.
`.claude/settings.json` denies `Bash(git *--output*)`, which closes the naive
spelling and nothing more: the shell reassembles adjacent quoted fragments
before `exec`, so `--out''put=<path>` reaches git as `--output=<path>` while
never presenting the matcher with a contiguous `--output`. Measured, not
reasoned about — `printf '%s' --out''put=/tmp/x` prints `--output=/tmp/x`.
`docs/harness-boundaries.md` records that as an accepted limit and names the
fix: "a helper that spells its own flags, or a rule over the executed argv
rather than the typed string". This is the second. It was `CLAUDE.md`'s
paragraph until the extraction; the pointer moved with the argument, because
this comment names the file to rewrite when the bound changes rather than
merely citing one.

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
# **A delimiter is a shell WORD, and matching an identifier-shaped prefix of
# one was a bypass.** `<<EOF-1` matched `EOF`, no `^EOF$` line was ever found,
# the whole tail was taken for an unterminated body — and the
# `git push origin +HEAD:main` after the real `EOF-1` line went with it.
# Measured: bash terminates on `EOF-1` and runs the push. Raised in review.
#
# `[ \t]*` rather than `\s*`, because a newline between `<<` and its delimiter
# is not a heredoc to bash either. The quote characters are spelled \x27 and
# \x22 so that neither this pattern nor anything quoting it has to escape them.
# **And a WORD may be quoted in PARTS, which matching one alternative could not
# express.** `<<E"OF"` names the delimiter `EOF` to bash and takes its body
# verbatim; the three-alternative form matched `<<E`, left `"OF"` standing where
# the subcommand goes, and `git <<E"OF" push origin +HEAD:main` was admitted
# while bash ran the push. Raised in review; verified allowed. So the word is
# one or more fragments — single-quoted, double-quoted, escaped or bare — and
# `_heredoc_delimiter` below does the quote removal the shell does.
#
# **`$'…'` is a quoting form and reading its `$` as bare was a fail-open.**
# `<<$'EOF'` names `EOF`; taking the `$` for an ordinary character made the
# delimiter `$EOF`, so a script terminating at a real `EOF` line had every
# command after it swallowed as body text — `git push origin +HEAD:main`
# included. Raised in review; verified allowed. `$"…"` is the locale form and
# is listed beside it for the same reason.
HEREDOC = re.compile(
    r"<<(?P<dash>-?)[ \t]*"
    r"(?P<word>(?:\$?\x27[^\x27]*\x27|\$?\x22[^\x22]*\x22|\\.|"
    r"[^\s;&|<>()\x27\x22\\])+)"
)


def _heredoc_delimiter(word):
    """The literal delimiter `word` names, and whether its body expands.

    Bash removes the quoting from a heredoc delimiter and expands the body only
    when the word carried **no** quoting at all — and the quoting may be
    partial, which is the whole of why this is a function rather than a group
    in the pattern. `<<E"OF"`, `<<"EOF"`, `<<'EOF'` and `<<\\EOF` all name
    `EOF` and all take their bodies verbatim; only a wholly bare `<<EOF`
    expands.

    **`$'…'` decodes escapes, and this returns `None` rather than guess one.**
    A delimiter the guard gets wrong is not symmetric: too long and the body
    swallows the commands after it, which is the fail-open this whole function
    exists to close. So an ANSI-C fragment carrying a backslash — the only part
    of the form that needs decoding — makes the delimiter unknown, and
    `heredoc_spans` then opens no body at all, leaving every following line to
    be judged as the command it may be. Erring toward refusing is the direction
    that costs a false positive rather than a force push.

    The pattern admits a fragment only in complete form, so every quote opened
    here is closed and the searches below cannot fail.
    """
    out, index, quoted = [], 0, False
    while index < len(word):
        char = word[index]
        if char == "$" and word[index + 1:index + 2] in ("'", '"'):
            quote = word[index + 1]
            close = word.index(quote, index + 2)
            body = word[index + 2:close]
            if quote == "'" and "\\" in body:
                return None, False
            out.append(body)
            index = close + 1
            quoted = True
            continue
        if char in "'\"":
            close = word.index(char, index + 1)
            out.append(word[index + 1:close])
            index = close + 1
            quoted = True
            continue
        if char == "\\" and index + 1 < len(word):
            out.append(word[index + 1])
            index += 2
            quoted = True
            continue
        out.append(char)
        index += 1
    return "".join(out), not quoted


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
    # **Whether a `#` begins a WORD, tracked rather than inferred from the
    # previous character.** The old test read `command[index - 1] in " \t…"`,
    # which cannot tell a separating space from an escaped one: in
    # `git log --grep=foo\\ #bar;git push origin +HEAD:main` bash keeps
    # `#bar` inside the `--grep` argument and runs the push, while the guard
    # read a comment and stripped the lot. Measured with a `git` shim. Raised
    # in review.
    at_word_start = True
    index = 0
    while index < len(command):
        char = command[index]
        if comment:
            if char == "\n":
                comment = False
                at_word_start = True
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
            elif char == "\\" and index + 1 < len(command):
                # An unquoted backslash escapes the next character, so that
                # character is ordinary text — a space included, and an escaped
                # space separates nothing.
                yield index, False, False
                yield index + 1, False, False
                index += 2
                at_word_start = False
                continue
            elif char == "'":
                single = True
                at_word_start = False
            elif char == '"':
                double = True
                at_word_start = False
            elif char == "#" and at_word_start:
                comment = True
                yield index, False, True
                index += 1
                continue
            elif char in METACHARACTERS:
                at_word_start = True
            else:
                at_word_start = False
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
        if not command.startswith("<<", index):
            continue
        # **`<<<` is a here-string, and it fed a push straight past this.**
        # The bare-delimiter alternative excludes `<`, so no opener matched at
        # the FIRST character of `<<<EOF` — and the scan then reached the
        # second one, where `<<EOF` matched perfectly. `cat <<<EOF` passes the
        # word `EOF` on stdin and the next line is an ordinary command:
        # measured, `EOF` is printed and the push runs. Raised in review.
        #
        # Two tests rather than one, because the operator has two ends. An
        # index inside a run of `<` is not the start of an operator, and an
        # operator that continues past `<<` is not a heredoc.
        if index > 0 and command[index - 1] == "<":
            continue
        if command.startswith("<<<", index):
            continue
        if not openers or index >= openers[-1][0]:
            match = HEREDOC.match(command, index)
            if match:
                # One parse of the delimiter word, quote removal included —
                # `<<\EOF` is a quoted delimiter to bash the same way `<<'EOF'`
                # is, and `<<E"OF"` is one in parts.
                delimiter, expands = _heredoc_delimiter(match.group("word"))
                if delimiter is None:
                    # A delimiter this file cannot decode opens no body, so the
                    # lines after it stay commands and are judged as such.
                    continue
                openers.append(
                    (match.end(), expands, delimiter, bool(match.group("dash"))))

    spans, pending = [], 0
    for intro_end, expands, delimiter, dash in openers:
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
        # **The terminator is the delimiter and nothing else.** `^\s*…\s*$`
        # accepted an indented or trailing-spaced line, and bash accepts
        # neither — only `<<-` strips leading TABS, and no form ignores
        # trailing whitespace. Measured: a heredoc body containing a line
        # `  EOF` prints it and keeps going. So an ordinary commit body that
        # indents the word had its remaining lines exposed as commands, which
        # is a false positive on exactly the file this repository writes most.
        # Raised in review.
        terminator = (
            rf"^\t*{re.escape(delimiter)}$" if dash
            else rf"^{re.escape(delimiter)}$")
        closing = re.search(terminator, command[start:], re.MULTILINE)
        if closing is None:
            # **No span, so nothing is stripped, and the fail direction is the
            # point.** A delimiter this guard cannot find means one of two
            # things: the heredoc really is unterminated, in which case the
            # tail is data and scanning it over-refuses a malformed command; or
            # the delimiter was read wrongly, in which case the tail holds
            # commands. Dropping it served the first and hid the second, and
            # the second is how `<<EOF-1` walked a push past this file.
            # Scanning is wrong only in the safe direction.
            break
        pending = start + closing.end()
        spans.append((start, pending, expands))
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


def join_continuations(command):
    """`command` with every line continuation removed, as bash removes them.

    **A backslash-newline is deleted before the shell tokenises anything**, so
    `git 2\\<newline>>&1 push origin +HEAD:main` reaches git as
    `git push origin +HEAD:main` with `2>&1` applied — and the guard, reading
    the backslash as an ordinary escape, stopped the descriptor scan at it,
    stripped `>&1` alone and left `2` sitting where the subcommand goes. The
    bare form `git \\<newline>push origin +HEAD:main` did the same thing with
    no descriptor at all. Both raised in review, both verified allowed, and
    both allowed on `main` before this file had a redirection strip.

    `separate_lines` deliberately keeps the pair — a continuation is not a
    separator — and that is still true; what was missing is that it is not an
    argument either. It is removed here, before anything reads a word, which is
    the order bash uses.

    **Inside single quotes a backslash is literal**, so a continuation there is
    two ordinary characters and stays. Inside double quotes bash removes it,
    and so does this.
    """
    out, index = [], 0
    in_single = in_double = False
    while index < len(command):
        char = command[index]
        if in_single:
            if char == "'":
                in_single = False
            out.append(char)
            index += 1
            continue
        if char == "\\" and index + 1 < len(command):
            if command[index + 1] == "\n":
                index += 2
                continue
            # Any other escape is passed through whole, so an escaped quote
            # never toggles the state below.
            out.append(char)
            out.append(command[index + 1])
            index += 2
            continue
        if char == "'" and not in_double:
            in_single = True
        elif char == '"':
            in_double = not in_double
        out.append(char)
        index += 1
    return "".join(out)


def separate_lines(command):
    """`command` with every unquoted newline turned into a `;`.

    **A newline separates commands, and `shlex` made it disappear.** With
    `whitespace_split=True` a newline is whitespace: it is never emitted as a
    token, so the `"\n"` in `SEPARATORS` matched nothing and every line of a
    script joined the run before it. Harmless while a `git` token anywhere was
    an invocation — and a bypass the moment `DATA_ONLY_COMMANDS` arrived, since

        echo hi
        git push origin +HEAD:main

    became one `echo`-led run and the push was exempt. Found while fixing a
    narrower case from review; the reported input was a comment inside a
    substitution, and this is why closing that one was not enough.

    A newline inside quotes is data and stays — `git commit -m "a<newline>b"`
    is one argument. So is one after a backslash, which is a line continuation
    bash removes rather than a separator.
    """
    out, escaped = [], None
    for index, in_quotes, in_comment in shell_positions(command):
        char = command[index]
        if index == escaped:
            out.append(char)
            escaped = None
            continue
        if char == "\\" and not in_quotes and not in_comment:
            escaped = index + 1
            out.append(char)
            continue
        if char == "\n" and not in_quotes and not in_comment:
            out.append(";")
        else:
            out.append(char)
    return "".join(out)


# Redirection operators, longest first so that `>>` is never read as a `>`
# with a stray `>` behind it. **`<<` and `<<<` are absent from this tuple
# because they are matched before it**, each by a branch of its own:
# `redirection_spans` argues both.
REDIRECTION_OPERATORS = ("&>>", "&>", ">>", ">&", ">|", "<>", "<&", ">", "<")


def redirection_spans(command):
    """Every redirection in `command`, as `(start, end)` character offsets.

    `start` is the first character of the file descriptor where one is written
    and of the operator otherwise, and `end` is just past the target word — so
    `command[start:end]` is everything bash consumes as redirection syntax and
    never hands to the program.

    **A heredoc introducer IS one of these, and an earlier revision of this
    docstring said the opposite.** The reasoning then was that `strip_heredocs`
    leaves the introducer standing so the line still tokenises, and that
    removing `<<` would strand its delimiter as a stray word. The second half
    was true and the conclusion did not follow: `<<` is whole punctuation, so
    leaving it made it a run boundary and severed `git` from its own
    subcommand — a fail-open. The introducer goes **with** its delimiter, which
    strands nothing, and `HEREDOC` is the one parse of that grammar this file
    has. A here-string is matched before either, since `<<<` has `<<` as a
    prefix.

    Raised in review, and the paragraph is kept in this shape deliberately: a
    docstring that still argued for the old behaviour is how the next edit
    restores it.
    """
    ordinary = [False] * len(command)
    escaped = None
    for index, in_quotes, in_comment in shell_positions(command):
        if index == escaped:
            escaped = None
            continue
        if command[index] == "\\" and not in_quotes and not in_comment:
            escaped = index + 1
            continue
        ordinary[index] = not in_quotes and not in_comment

    def plain(position):
        return position < len(command) and ordinary[position]

    def word_end(position):
        """The end of the redirect target WORD beginning at `position`.

        **A substitution is part of the word, and stopping at its `(` was a
        fail-open.** A word ends at an unquoted metacharacter — but the `(` of
        `$(…)` is not one to bash, it opens a nested command list. Stopping
        there left the parentheses standing, `is_boundary` read them as run
        boundaries, and `git >/tmp/$(echo x) push origin +HEAD:main` had its
        `git` severed from its own subcommand: the force push ran and the guard
        admitted it. Raised in review; verified allowed, with `$((…))`, a bare
        `$(…)` target and a backtick spelling beside it.

        An UNBALANCED opener stops the word instead of swallowing the rest of
        the line, because consuming to the end would hide whatever followed —
        the same fail-open one layer along.

        **And a word may not BEGIN with `(`, which is the difference between a
        substitution inside a target and a process substitution being one.**
        `echo <(git push origin +HEAD:main)` is not a redirect with `(…)` for a
        target: `<(` is one construct, the inner command runs, and consuming it
        as a word deleted that push from the judged string outright. Caught by
        `test_a_process_substitution_is_not_the_printers_argument`, which is
        why it exists — the same reading applies to `> >(tee f)`, whose target
        is a process substitution that also runs. Left alone, the parentheses
        stay the run boundaries they already were and the inner command is
        judged in its own right.
        """
        first = position
        while position < len(command):
            char = command[position]
            if not ordinary[position]:
                position += 1
                continue
            if char == "`":
                # **Escape-aware, because `\`` is how the legacy form nests.**
                # A plain `find` ended the word at the inner delimiter of
                # `` >/tmp/`echo \`echo x\`` `` and left the outer backtick
                # sitting where the subcommand goes. `substitutions` already
                # scans this way; the two agree on purpose.
                scan = position + 1
                while scan < len(command):
                    if command[scan] == "\\" and scan + 1 < len(command):
                        scan += 2
                        continue
                    if command[scan] == "`":
                        break
                    scan += 1
                if scan >= len(command):
                    return position
                position = scan + 1
                continue
            if command.startswith("${", position):
                # **A parameter expansion is part of the word, metacharacters
                # and all.** `>${PATH:+/tmp/x;y}` redirects to `/tmp/x;y`, and
                # returning at that `;` left a separator standing between `git`
                # and its subcommand.
                close = _closing_brace(command, position + 2)
                if close is None:
                    return position
                position = close + 1
                continue
            if char == "(":
                if position == first:
                    return position
                close = _closing_paren(command, position + 1)
                if close is None:
                    return position
                position = close + 1
                continue
            if char in METACHARACTERS:
                return position
            position += 1
        return position

    spans, index = [], 0
    while index < len(command):
        if not ordinary[index]:
            index += 1
            continue
        start = index
        digits = index
        while plain(digits) and command[digits].isdigit():
            digits += 1
        if digits == start and command[start] == "{":
            # **The descriptor grammar is not only digits**, and reading it as
            # digits alone left `git {fd}>&1 push origin +HEAD:main` admitted
            # while bash ran the force push: `>&1` went, `{fd}` stayed, and
            # `push_offence` took that word for the subcommand and stopped
            # looking. Raised in review on the change that closed the digit
            # half; verified allowed before the fix. Bash takes `{name}` where
            # name is an identifier, so a leading digit is not one.
            close = start + 1
            if plain(close) and (command[close].isalpha() or command[close] == "_"):
                while plain(close) and (command[close].isalnum()
                                        or command[close] == "_"):
                    close += 1
                if plain(close) and command[close] == "}":
                    digits = close + 1
        begins_word = start == 0 or (
            ordinary[start - 1] and command[start - 1] in METACHARACTERS)
        if digits > start and not begins_word:
            # **A descriptor is a WHOLE token glued to the operator**, which is
            # bash's own rule rather than an approximation of it: in
            # `echo foo2>x` the word bash writes is `foo2` and only `>x` is
            # syntax. Reading the digits here would be editing an argument,
            # which is the thing `shell_positions` exists to stop this file
            # doing.
            index = digits
            continue
        if command[digits:digits + 3] == "<<<":
            # A here-string's word is data the shell feeds in, exactly like a
            # redirect target — and it is checked before `<<`, which is a
            # prefix of it. Left to the branch below, `<<<x` was reduced to a
            # bare `<<` that still split the run.
            end = digits + 3
            while plain(end) and command[end] in " \t":
                end += 1
            end = word_end(end)
            spans.append((start, end))
            index = end
            continue
        if command[digits:digits + 2] == "<<":
            # **A heredoc introducer goes WITH its delimiter, and leaving it
            # standing was a fail-open.** `strip_heredocs` takes the body and
            # leaves this behind so the rest of the line still tokenises — but
            # `<<` is whole punctuation, so `is_boundary` ends the run there:
            # in `git <<EOF push origin +HEAD:main` the `git` token was severed
            # from its own subcommand, `git_segments` yielded nothing, and bash
            # ran the force push. Raised in review; verified allowed, and
            # allowed on `main` before this file grew a strip at all.
            #
            # Removing the delimiter with it is what leaves no stray word, and
            # `HEREDOC` is the one parse of that grammar this file has — the
            # dash form and both quoted spellings included.
            introducer = HEREDOC.match(command, digits)
            if introducer is not None:
                spans.append((start, introducer.end()))
                index = introducer.end()
                continue
            # An introducer this file cannot parse keeps its old treatment, and
            # a descriptor in front of one is still the stray word every other
            # spelling leaves.
            if digits > start:
                spans.append((start, digits))
            index = digits + 2
            continue
        operator = None
        for candidate in REDIRECTION_OPERATORS:
            reach = range(digits, digits + len(candidate))
            if command[digits:digits + len(candidate)] == candidate and all(
                    plain(position) for position in reach):
                operator = candidate
                break
        if operator is None:
            index = digits + 1 if digits == start else digits
            continue
        end = digits + len(operator)
        while plain(end) and command[end] in " \t":
            end += 1
        # **A process substitution can BE the target, and leaving it to the run
        # splitter hides the outer command.** In
        # `git > >(tee /tmp/log) push origin +HEAD:main` both `>` characters
        # were removed separately and `(tee /tmp/log)` stayed as a boundary
        # between `git` and `push` — bash runs the force push and the guard
        # admitted it. Raised in review, twice: the round before this one
        # asserted in a comment that the run splitter covered this case, which
        # was true of the INNER command and false of the outer one.
        #
        # So it is consumed as the word it is, and `substitutions` grew the
        # same construct in the same change — a target nothing judged would be
        # the hole this one closes, one layer along.
        if (command[end:end + 2] in (">(", "<(")
                and plain(end) and plain(end + 1)):
            close = _closing_paren(command, end + 2)
            if close is not None:
                spans.append((start, close + 1))
                index = close + 1
                continue
        end = word_end(end)
        spans.append((start, end))
        index = end
    return spans


def strip_redirections(command):
    """`command` with every redirection removed, target word included.

    **A redirection is shell syntax and the file descriptor in front of one is
    not — to `shlex`.** `punctuation_chars=True` emits a maximal run of
    `();<>|&` as ONE token, so `>&` arrives whole, but a digit is not
    punctuation: the `2` of `2>&1` detaches and survives as an ordinary word.
    That one stray word reached every check downstream that counts non-flags,
    in three separate directions (#183):

        git push -u origin feat 2>&1        three positionals where two are
                                            required, so an honest push was
                                            refused for naming two refspecs
        git push -u origin 2>&1 +HEAD:main  `2` taken for the refspec — it
                                            satisfies `SAFE_REF` — while the
                                            real one fell past the `>&`
                                            boundary into a run of its own: a
                                            FORCE PUSH TO MAIN, admitted
        git 2>&1 log --output=/tmp/probe    the run split at `>&`, the second
                                            run led with `1` and held no `git`
                                            token, so #30's write primitive was
                                            admitted

    All measured against the guard as shipped. Removing the whole redirection
    is what makes the remaining string the argv bash passes to the program,
    which is the one thing this hook claims to judge — and it is one strip in
    the pipeline both paths read rather than a relaxed count in whichever check
    someone happened to be looking at.
    """
    out, cursor = [], 0
    for start, end in redirection_spans(command):
        out.append(command[cursor:start])
        cursor = end
    out.append(command[cursor:])
    return "".join(out)


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
    # **And double-quote state for ONE branch.** A process substitution is not
    # performed inside double quotes — `echo "<(x)"` prints the text — so the
    # branch added for it is gated on this, where `$(` deliberately is not.
    # Reading it anywhere else would resurrect the bypass the comment above
    # names.
    in_double = False
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
        if char == '"' and quotes:
            in_double = not in_double
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
        if quotes and not in_double and (command.startswith("<(", index)
                                         or command.startswith(">(", index)):
            # **A process substitution is a command the shell runs**, and until
            # the redirection strip could consume one it was reached only by
            # the run splitter — which sees it while it stands as its own run
            # and not once it is part of a redirect target. Both halves of that
            # are now true in one place.
            #
            # **`quotes` is false for a heredoc BODY, and a body performs no
            # process substitution** — parameter, command and arithmetic
            # expansion only. Reading one there made literal prose executable,
            # so a heredoc quoting `<(git push …)` as an example was refused.
            # Raised in review; measured, and it is the over-refusal this
            # file's own docstring says gets a guard turned off. The flag is
            # reused rather than a second one added, because it already means
            # "this region is a command line" everywhere it is passed.
            end = _closing_paren(command, index + 2)
            if end is None:
                break
            found.append(command[index + 2:end])
            index = end + 1
            continue
        if command.startswith("${", index) and command[index + 2:index + 3] in (
                " ", "\t", "\n", "|"):
            # **bash 5.3's function substitution runs a command**, where every
            # other `${…}` expands a parameter and runs nothing. `${ cmd; }`
            # and `${| cmd; }` are the two spellings, and the character after
            # the brace is what separates them from `${VAR}`.
            #
            # **This host is 5.2.26 and does not support it** — measured,
            # `bad substitution` — so it is closed BEFORE it is reachable
            # rather than after. An exemption resting on a version is one that
            # expires silently, and this file already carries that lesson about
            # a hook directory that was safe until it was not.
            end = _closing_brace(command, index + 2)
            if end is None:
                break
            found.append(command[index + 2:end].lstrip("| \t\n"))
            index = end + 1
            continue
        if char == "`":
            # `find` ignored escapes, and a `\`` is a literal backtick to bash
            # rather than a terminator. Raised in review. **The reported
            # example is a bash SYNTAX ERROR** — measured, `unexpected EOF
            # while looking for matching` — so it was never a live bypass; the
            # scan is corrected anyway, because agreeing with the shell about
            # where a substitution ends is the property, not the one input that
            # exposed it.
            end = index + 1
            while end < len(command):
                if command[end] == "\\" and end + 1 < len(command):
                    end += 2
                    continue
                if command[end] == "`":
                    break
                end += 1
            if end >= len(command):
                break
            # **An escaped backtick is how the legacy form NESTS**, so skipping
            # the escape and handing the body on unchanged skipped it twice:
            # `git log "`echo \`git push origin +HEAD:main\``"` runs the push
            # — measured — and the inner substitution was invisible to both
            # passes. Unescaping on the way down is what makes the recursion
            # see the nested command as a command.
            found.append(command[index + 1:end].replace("\\`", "`"))
            index = end + 1
            continue
        index += 1
    return found


def _closing_brace(command, start):
    """Index of the `}` closing a function substitution, or None.

    The same quote-and-escape tracking `_closing_paren` does, one bracket over.
    Written as its own function rather than parameterised, because the two
    differ in what nests inside them and a shared one would have to be told.
    """
    depth, index = 1, start
    single = double = comment = False
    at_word_start = True
    while index < len(command):
        char = command[index]
        if comment:
            # A function substitution's body is a command list too, so a `}`
            # inside a comment closes nothing. The same handling
            # `_closing_paren` carries, and owed here for the same reason —
            # raised in review, one bracket over.
            if char == "\n":
                comment = False
                at_word_start = True
            index += 1
            continue
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
            index += 2
            at_word_start = False
            continue
        elif char == "#" and at_word_start:
            comment = True
            index += 1
            continue
        elif char == "'":
            single = True
            at_word_start = False
        elif char == '"':
            double = True
            at_word_start = False
        elif char == "{":
            depth += 1
        elif char == "}":
            depth -= 1
            if not depth:
                return index
        if not (single or double):
            at_word_start = char in METACHARACTERS
        index += 1
    return None


def _closing_paren(command, start):
    """Index of the `)` closing a substitution opened before `start`, or None.

    **Quotes are tracked while balancing**, because a paren counter that reads
    raw characters closes early on a quoted one:
    `git log "$(printf ')'; git push origin +HEAD:main)"` ended extraction at
    the `)` inside `'…'`, leaving the push hidden in the outer token. Raised in
    review; verified allowed.
    """
    depth, index = 1, start
    single = double = comment = False
    at_word_start = True
    while index < len(command):
        char = command[index]
        if comment:
            # **A substitution's body is a command list, so `#` opens a comment
            # inside it and a `)` in that comment closes nothing.**
            # `git log "$(echo ok # )` / `git push origin +HEAD:main)"` ended
            # extraction at the commented paren and left the push in the outer
            # token — measured, bash runs it. Raised in review.
            if char == "\n":
                comment = False
                at_word_start = True
            index += 1
            continue
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
            at_word_start = False
            continue
        elif char == "#" and at_word_start:
            comment = True
            index += 1
            continue
        elif char == "'":
            single = True
            at_word_start = False
        elif char == '"':
            double = True
            at_word_start = False
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
        if not (single or double):
            at_word_start = char in METACHARACTERS
        index += 1
    return None


# A shell invoked with `-c` runs its argument as a command line, and `eval` runs
# the concatenation of its own. Both hand the guard a command it must read as
# one rather than as data.
EVALUATORS = {"bash", "sh", "dash", "zsh", "ksh"}

# `-c`, and the bundles that carry it — `bash -xc <script>` and `bash -cx
# <script>` alike. **`c` need not come last**, which the first version of this
# required: `bash -cx 'git push origin +HEAD:main'` runs the push, measured,
# and matched nothing. Raised in review. A long option is never the script
# introducer, so `--` forms are left alone.
SCRIPT_FLAG = re.compile(r"^-[A-Za-z]*c[A-Za-z]*$")


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


def evaluated_scripts(tokens):
    """Every token a shell evaluator in `tokens` will execute as a command.

    **`shlex` hands a quoted script back as one data token**, exactly as it does
    a command substitution — so `git log "$(bash -c 'git push origin
    +HEAD:main')"` reached the inner pass as `bash`, `-c` and one opaque string,
    the segment scan found no `git`, and the push ran. Raised in review;
    verified allowed, and the bash behaviour measured with a `git` shim.

    **The data-only boundary applies here too**, and it did not at first: a run
    led by `echo` is text, so `echo bash -c \'git push …\'` was refused for
    quoting a command. Raised in review — the same false-positive class the
    boundary was added to close, left standing in the pass beside it, which is
    this repository\'s most-repeated shape.

    **The bound: a script this hook can READ.** `bash script.sh` runs a file,
    and a hook is handed an argv rather than a filesystem — that is outside what
    any argv guard can see, and it is the same shape as the parameter-expansion
    residual rather than a new one.
    """
    for run in command_runs(tokens):
        if not run or program_name(run[0]) in DATA_ONLY_COMMANDS:
            continue
        for index, token in enumerate(run):
            name = program_name(token)
            if name in EVALUATORS:
                argv = run[index + 1:]
                for position, element in enumerate(argv):
                    if SCRIPT_FLAG.match(element) and position + 1 < len(argv):
                        yield argv[position + 1]
                        break
            elif name == "eval":
                argv = run[index + 1:]
                if argv:
                    yield " ".join(argv)


# Commands whose arguments are text and never a command line.
#
# **An allow-list, and the direction is load-bearing.** A name missing from
# here costs an over-refusal; the converse — listing the wrappers that DO
# execute their arguments — fails open on the first one nobody thought of, and
# `timeout`, `env`, `nohup`, `xargs`, `sudo`, `command` and `time` all run
# `git push origin +HEAD:main` perfectly well. A run led by anything not named
# here keeps reaching the scan.
DATA_ONLY_COMMANDS = {"echo", "printf", ":", "true", "false"}


# `shlex(punctuation_chars=True)` emits a maximal RUN of these as ONE token, so
# an operator can arrive glued to its neighbour and match no separator by name.
PUNCTUATION = set("();<>|&")

# What bash treats as a word separator when unquoted. **Not the same set as
# PUNCTUATION**, which is `shlex`'s: this one carries the whitespace, because
# the question it answers is where a WORD begins rather than where a token
# does.
METACHARACTERS = set("|&;()<> \t\n")


def is_boundary(token):
    """Whether `token` ends the command run it appears in.

    **`);` is one token, and it matched nothing.** So
    `git log -1; (echo ok);git push origin +HEAD:main` left the push inside a
    run still led by `echo`, the data-only exemption skipped it, and bash ran
    it — measured with a `git` shim. Raised in review.

    A token made entirely of shell punctuation is a boundary whatever it is
    glued into, which also settles `<(`: a process substitution is executed
    BEFORE the command it is an argument to, so the `git` inside one belongs to
    no printer's run. `echo <(git push origin +HEAD:main)` ran the push too,
    measured the same way, and both are one question about where a run ends.
    """
    return token in SEPARATORS or (
        token != "" and all(char in PUNCTUATION for char in token))


def command_runs(tokens):
    """`tokens` split into the separate commands the shell would run."""
    current = []
    for token in tokens:
        if is_boundary(token):
            if current:
                yield current
            current = []
        else:
            current.append(token)
    if current:
        yield current


def git_segments(tokens):
    """Yield the argv slice of every `git` invocation in a compound command.

    **A `git` token is only an invocation where a command can stand.**
    `echo git push origin +HEAD:main` was refused, and a guard that refuses
    honest traffic is the one this file's own docstring says somebody turns
    off. Raised in review.

    The test is the run's LEADING word, not where `git` sits inside it, because
    a wrapper puts the real command in the middle — which is why the scan still
    covers the whole run.
    """
    for run in command_runs(tokens):
        if not run or program_name(run[0]) in DATA_ONLY_COMMANDS:
            continue
        for index, token in enumerate(run):
            if program_name(token) != "git":
                continue
            yield run[index + 1:]


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
    #
    # `strip_redirections` is outermost because it is the only one of the four
    # that wants the others' work done first: a redirection inside a heredoc
    # body or a comment is not one bash performs, and there is nothing left of
    # either by the time it runs.
    # `join_continuations` sits after `strip_comments` because a backslash at
    # the end of a COMMENT continues nothing — bash ends a comment at the
    # newline — so joining first would have swallowed the next line into it.
    stripped = strip_redirections(
        separate_lines(
            join_continuations(strip_comments(strip_heredocs(command)))))
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
            # `-cdiff.external=<cmd>` was raised in review as a compact form
            # git accepts. **It does not**, on 2.45.1: `unknown option`, and
            # the usage line spells the option `-c <name>=<value>`. So this is
            # hardening rather than a fix, and it is cheap because the global
            # option set is small and fixed — no git subcommand flag can reach
            # here, since this loop only ever sees the tokens BEFORE the
            # subcommand. `-C` is left alone, and the comparison is
            # case-sensitive for exactly that reason.
            if element in CONFIG_OPTIONS or element.startswith("-c") or any(
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
            # **git accepts any unambiguous ABBREVIATION of a long option**,
            # so a canonical-prefix test reads less than it looks like it does.
            # Measured against a real remote: `--upload-p=<cmd>` and even
            # `--upl=<cmd>` are accepted by `git fetch` and the command RUNS;
            # only `--u` is refused, and for being ambiguous rather than
            # unknown. Raised in review.
            #
            # So the test runs both ways — the element starting with a
            # forbidden flag, and a forbidden flag starting with the element.
            # An abbreviation of something harmless that happens to prefix one
            # of these is refused too; that is over-refusal, which is the
            # direction to be wrong in, and `--u` was never going to work.
            name = element.split("=", 1)[0]
            abbreviation = name.startswith("--") and len(name) > 2
            for flag in FORBIDDEN_FLAGS:
                if element.startswith(flag) or (
                        abbreviation and flag.startswith(name)):
                    return (
                        f"`git ... {flag}` is refused: it writes or executes "
                        "rather than inspects, and the settings deny it matches "
                        "only the unquoted spelling. This hook compares the "
                        "resolved argv, and any unambiguous abbreviation of it."
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
