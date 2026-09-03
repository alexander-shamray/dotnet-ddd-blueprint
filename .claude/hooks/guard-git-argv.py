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
import traceback

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
#
# **A continuation may split the delimiter itself**, and the word class has to
# say so before anything else can. `<<EO\<newline>F` names `EOF` to bash, which
# removes the pair at the input level; reading the delimiter as `EO` made the
# guard's body start a line early and end a line early, so the real command
# line was swallowed as data and `git <<EO\<newline>F push origin +HEAD:main`
# was admitted. `join_continuations` cannot help here — `strip_heredocs` runs
# on the raw command, before it, and must, because a heredoc body is not a
# command line. Raised in an adversarial audit; verified allowed, on `main` as
# well. `\\\n` leads the alternatives because `\\.` cannot match a newline.
#
# **A quoted fragment ends at an UNESCAPED quote and never spans a line.**
# `<<"E\\"OF"` names `E"OF` to bash; the fragment closed at the escaped quote,
# the scan then ran on across the newline and took the next line into the
# word, and the delimiter came out as nonsense — so `heredoc_spans` found no
# body at all. That direction happened to refuse; the mirror of it, where the
# nonsense delimiter matches a line the payload plants, swallows whatever sits
# between. Raised in review.
HEREDOC = re.compile(
    r"<<(?P<dash>-?)[ \t]*"
    r"(?P<word>(?:\\\n"
    r"|\$?\x27[^\x27\n]*\x27"
    r"|\$?\x22(?:[^\x22\\\n]|\\[^\n])*\x22"
    r"|\\[^\n]"
    r"|[^\s;&|<>()\x27\x22\\])+)"
)


def _sigil_quote(word, index):
    """Where the quote of a `$'…'`/`$"…"` at `index` starts, or None.

    Line continuations between the two are skipped, because bash removes them
    before it reads the word.
    """
    peek = index + 1
    while word[peek:peek + 2] == "\\\n":
        peek += 2
    return peek if word[peek:peek + 1] in ("'", '"') else None


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
        if char == "$" and _sigil_quote(word, index) is not None:
            # **A continuation between the sigil and its quote does not break
            # the pairing**, because bash removes the pair before it reads the
            # word: `<<$\<newline>'EOF'` names `EOF`. Reading the `$` as an
            # ordinary character gave `$EOF`, so the real `EOF` line terminated
            # nothing and every command after it was swallowed as body text.
            #
            # Raised in review, and answered once before it was true: the case
            # passed at the time for an unrelated reason — one of the expansion
            # readings happened to rewrite inside the body — and only stopped
            # passing when those readings were correctly stopped from rewriting
            # a body that expands nothing. A test that passes for a reason
            # nobody has checked is one that reports the wrong thing later.
            peek = _sigil_quote(word, index)
            quote = word[peek]
            close = word.index(quote, peek + 1)
            body = word[peek + 1:close]
            if quote == '"':
                # **A locale-quoted delimiter is TRANSLATED**, exactly as
                # a locale-quoted word is, and this branch was reading
                # `$"EOF"` as the literal `EOF` while
                # `undecodable_dollar_quote` refused the same construct
                # three functions along. A catalogue naming `EOF` for
                # `safe` ends the body where bash does not, and a push
                # between the two lines is swallowed or exposed depending
                # on which way the mismatch falls. Raised in review, which
                # also caught the test that had just pinned `$"EOF"` as
                # literal — the assertion and the defect landed together.
                return None, False
            # **Either sigil, and an earlier revision refused only the
            # ANSI-C one.** `<<$"E\\"OF"` names `E"OF` to bash, where
            # `word.index` finds the ESCAPED quote and derives `E\\OF` — a
            # delimiter that matches nothing, so a line the payload plants
            # can close the body early or late and take an intervening
            # push with it. Raised in review; measured, and the reasoning
            # that let the two sigils differ was that a locale quote
            # "carries double-quote semantics", which is exactly why its
            # closer is not the first quote.
            if "\\" in body:
                return None, False
            out.append(body)
            index = close + 1
            quoted = True
            continue
        if char == "'":
            close = word.index(char, index + 1)
            out.append(word[index + 1:close])
            index = close + 1
            quoted = True
            continue
        if char == '"':
            # Double quotes carry escapes, so the closer is the first UNESCAPED
            # one and `\"` contributes a quote rather than ending the fragment.
            scan, body = index + 1, []
            while scan < len(word):
                if word[scan] == "\\" and scan + 1 < len(word):
                    body.append(word[scan + 1] if word[scan + 1] in '$`"\\'
                                else word[scan:scan + 2])
                    scan += 2
                    continue
                if word[scan] == '"':
                    break
                body.append(word[scan])
                scan += 1
            out.append("".join(body))
            index = scan + 1
            quoted = True
            continue
        if char == "\\" and word[index + 1:index + 2] == "\n":
            # A continuation inside the delimiter contributes nothing and
            # quotes nothing — bash removes the pair before it reads the word.
            index += 2
            continue
        if char == "\\" and index + 1 < len(word):
            out.append(word[index + 1])
            index += 2
            quoted = True
            continue
        out.append(char)
        index += 1
    return "".join(out), not quoted


def shell_positions(command, data=()):
    """Walk `command`, yielding `(index, in_quotes, in_comment)` per character.

    One of two adapters over `_quoting`, which is where the model lives.
    """
    for index, state, _escaped in _quoting(command, data=data):
        yield index, state in ("single", "double", "data"), state == "comment"


def quote_states(command, quotes=True):
    """`command`'s quoting state at each character.

    `state[i]` is `"single"`, `"double"`, `"comment"` or `""`. A scanner that
    also needs to know where an escape sits keeps its own backslash branch:
    every caller here already had one, and they differ — a continuation join
    deletes the pair, an expansion rewrite copies it through.

    **This exists because five scanners in this file each carried their own
    copy of bash's quote rules, and none of them learned about `$'…'`.**
    `shell_positions` was made escape-aware for it, and
    `without_substitutions`, `rewriting_expansions`, `dollar_quotes`,
    `join_continuations` and `substitutions` were not — so `: $'x\'';` in
    front of a command left every one of them one quote out of step, and
    `$( )`, `${x:-push}`, a line continuation and a nested `$(git push …)`
    each walked past the pass that exists to catch it. Four of the five were
    verified allowed; all five are raised in review. **A fix that lands in one
    function and not in its siblings is this file's most-repeated failure**,
    and the answer is not a sixth careful copy.

    `quotes` is false for a heredoc BODY, where a quote is an ordinary
    character — the same flag its callers already take.
    """
    states = [""] * len(command)
    for index, state, _escaped in _quoting(command, quotes=quotes):
        states[index] = state
    return states


def _quoting(command, quotes=True, data=()):
    """Walk `command`, yielding `(index, state, escaped)` per character.

    `data` is spans this scanner must read as data rather than as shell text —
    heredoc bodies, and only `heredoc_spans` passes any. Inside one, a quote
    opens nothing and a `#` starts nothing: the characters are yielded as
    quoted, which is what every consumer of this scanner means by "not a
    command line".

    **One scanner, because both callers were defeated by the same thing.** A
    regex search for `<<` found a heredoc opener inside a COMMENT, so
    `git status # <<EOF` swallowed the real command on the next line before it
    could be judged; and a paren counter that did not know about quotes let
    `git log "$(printf ')'; git push origin +HEAD:main)"` close early, hiding
    the push in the outer token. Both raised in review, both verified allowed.

    `state` is `"single"` inside `'…'`, `"double"` inside `"…"`, `"comment"`
    in a comment, `"data"` inside one of `data`'s spans and `""` elsewhere.
    Comments start at an unquoted `#` that begins a word and end at the
    newline — which is bash's rule, and the reason `git log --grep=#x` is not
    a comment.

    **An ANSI-C word takes a backslash and an ordinary single-quoted one does
    not**, and reading `$'…'` by the ordinary rule desynchronised every
    consumer of this scanner from the position it was on. `$'''` is the
    one-character word `'` to bash — the escaped quote does not close it — so
    `$''' ; git 2>&1 push origin +HEAD:main` runs the push. Read by the
    ordinary rule the word closes at the escaped quote, the quote after it
    opens one that never closes, and the rest of the line is `in_quotes`: so
    `redirection_spans` left `2>&1` standing, `is_boundary` read the glued
    `>&` as a run boundary, and `git` was severed from its own subcommand.
    Raised in review; verified allowed. `$"…"` needs nothing, because a
    locale-quoted word already follows the double-quoted rule this scanner
    applies to it.
    """
    # **Not copied and not sorted**: `heredoc_spans` appends to this list
    # while consuming the generator, and every span it appends starts ahead of
    # the cursor, so the order holds by construction.
    single = double = comment = False
    # Whether the single quote now open was introduced by a `$`, and whether
    # the character just yielded was an unquoted, unescaped `$`.
    ansi_c = dollar = False
    # **Whether a `#` begins a WORD, tracked rather than inferred from the
    # previous character.** The old test read `command[index - 1] in " \t…"`,
    # which cannot tell a separating space from an escaped one: in
    # `git log --grep=foo\\ #bar;git push origin +HEAD:main` bash keeps
    # `#bar` inside the `--grep` argument and runs the push, while the guard
    # read a comment and stripped the lot. Measured with a `git` shim. Raised
    # in review.
    at_word_start = True
    index = 0
    # Whether this character is the one the backslash before it escapes.
    pending = False
    # A cursor rather than a search: this walk is monotonic, so the spans are
    # consumed in order. Searching them per character made a command carrying
    # 200 heredocs ten times slower, and a quadratic path in this file is the
    # shape that produced the memoisation fix.
    cursor = 0
    while index < len(command):
        while cursor < len(data) and data[cursor][1] <= index:
            cursor += 1
        if cursor < len(data) and data[cursor][0] <= index:
            while index < data[cursor][1] and index < len(command):
                yield index, "data", False
                index += 1
            at_word_start = True
            dollar = False
            continue
        char = command[index]
        if comment:
            if char == "\n":
                comment = False
                at_word_start = True
            else:
                yield index, "comment", False
                index += 1
                continue
        if not comment:
            if single:
                if ansi_c and char == "\\" and index + 1 < len(command):
                    # Both characters, for `strip_comments`' reason below: a
                    # consumer rebuilds text from these positions.
                    yield index, "single", False
                    yield index + 1, "single", True
                    index += 2
                    dollar = False
                    continue
                if char == "'":
                    single = ansi_c = False
            elif double:
                if char == "\\" and index + 1 < len(command):
                    # **Both characters, because a consumer rebuilds text from
                    # these positions.** Yielding only the backslash made
                    # `strip_comments` DELETE the escaped character, so
                    # `git log "$(printf \); git push …)"` lost its `)` and
                    # changed shape on its way through the guard. A scanner that
                    # silently edits its input is worse than one that misreads
                    # it, because every later stage inherits the edit.
                    yield index, "double", False
                    yield index + 1, "double", True
                    index += 2
                    dollar = False
                    continue
                if char == '"':
                    double = False
            elif char == "\\" and index + 1 < len(command):
                # An unquoted backslash escapes the next character, so that
                # character is ordinary text — a space included, and an escaped
                # space separates nothing.
                yield index, "", False
                yield index + 1, "", True
                index += 2
                at_word_start = False
                dollar = False
                continue
            elif char == "'" and quotes:
                single = True
                ansi_c = dollar
                at_word_start = False
            elif char == '"' and quotes:
                double = True
                at_word_start = False
            elif char == "#" and at_word_start:
                comment = True
                yield index, "comment", False
                index += 1
                dollar = False
                continue
            elif char in METACHARACTERS:
                at_word_start = True
            else:
                at_word_start = False
        yield index, ("single" if single else "double" if double
                      else "comment" if comment else ""), pending
        pending = False
        dollar = char == "$" and not (single or double or comment)
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
    # **A body is data, and its quotes are not the command line's.** An
    # apostrophe in one used to open a quote that ran to the end of the
    # command, so every later opener sat `in_quotes`, was skipped, and its
    # body was left standing to be tokenised as commands. Found by hitting it:
    # writing four replies to disk with `cat > f <<'EOF'` heredocs was refused
    # because a body quoting `bash -c` reached the evaluator scan. Over-refusal
    # in every direction probed — a push after such a body was refused before
    # and after — which is how it survived this long.
    #
    # **`data` is handed to the scanner and appended to WHILE it walks**, which
    # is what makes this one pass. Feeding the spans back between whole passes
    # instead recovers exactly one body per pass, because each newly visible
    # body breaks the state again at its own apostrophe: measured at n+1 passes
    # for n heredocs, which is the quadratic shape this file already treats as
    # a fail-open by timeout. The scanner consumes `data` through a cursor and
    # this loop only ever appends spans that start ahead of it, so the list is
    # sorted by construction and the walk stays monotonic.
    data, spans, pending = [], [], 0
    for index, in_quotes, in_comment in shell_positions(command, data):
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
        match = HEREDOC.match(command, index)
        if not match:
            continue
        # One parse of the delimiter word, quote removal included —
        # `<<\EOF` is a quoted delimiter to bash the same way `<<'EOF'`
        # is, and `<<E"OF"` is one in parts.
        delimiter, expands = _heredoc_delimiter(match.group("word"))
        if delimiter is None:
            # A delimiter this file cannot decode opens no body, so the
            # lines after it stay commands and are judged as such.
            continue
        intro_end, dash = match.end(), bool(match.group("dash"))

        # An introducer sitting inside an earlier body is body text, not an
        # opener — which the scanner now settles by refusing to walk a body at
        # all, so the containment test that used to stand here is gone rather
        # than kept as a second answer to one question. Two heredocs stacked on
        # ONE line both introduce before either body starts, and that is still
        # `pending`'s job below rather than an ordering test's.

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
        data.append((start, pending))
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


def undecodable_heredoc(command):
    """Whether a heredoc names a delimiter this file cannot read.

    **"Open no body and let the lines be judged" was the wrong fail-safe, and
    review took it apart.** The reasoning was that a body left unstripped is
    read as commands, which refuses rather than admits — true only while the
    command still tokenises. A body carrying an unmatched quote sends `offence`
    down its `ValueError` path, and that fallback scans for forbidden flags and
    `ext::` alone: it does not enforce the push allow-list, so
    `git commit -F - <<$'E\\x4fF'` with such a body admitted a force push.
    Measured.

    So an undecodable delimiter is refused outright rather than worked around.
    The alternative is decoding every ANSI-C escape bash supports, which is a
    list that trails bash's — the shape this file refuses elsewhere — and each
    gap in it would reopen exactly this hole.

    The scan asks `shell_positions` where the `<<` is, so a delimiter quoted
    inside an argument is not one of these; the two guards below are
    `heredoc_spans`', for the same reasons it states.
    """
    # **The bodies are computed FIRST and handed to the scanner**, which is
    # the same fix `heredoc_spans` took one function above and the same
    # oversight arriving in the function beside it: an apostrophe in an earlier
    # body left the scanner in quote state, so a later undecodable opener
    # looked quoted and this refusal never fired. Raised in review.
    bodies = heredoc_spans(command)
    quoted = set()
    for index, in_quotes, in_comment in shell_positions(
            command, [(start, end) for start, end, _ in bodies]):
        if in_quotes or in_comment:
            quoted.add(index)
    # **A `<<` inside a heredoc BODY is data, not an opener**, and reading one
    # as an opener refused an innocent filing: a body quoting `<<$'E\\x4fF'` —
    # documentation of this very mechanism — was rejected as an undecodable
    # delimiter. `heredoc_spans` is what knows where a body is, which is why it
    # is asked above rather than here.
    for match in HEREDOC.finditer(command):
        index = match.start()
        if index in quoted:
            # A body is among the spans handed to the scanner above, so an
            # opener inside one arrives quoted and this is where it stops.
            # **The containment test that used to stand here as well was the
            # quadratic** — 3,200 heredocs meant ten million comparisons, and
            # the hook's timeout is empty stdout, which is non-blocking. Raised
            # in review against `stdin_scripts`, where the same test sat for
            # the same reason; this copy was found by profiling the fix.
            continue
        if index > 0 and command[index - 1] == "<":
            continue
        if command.startswith("<<<", index):
            continue
        delimiter, _expands = _heredoc_delimiter(match.group("word"))
        if delimiter is None:
            return True
    return False


def expansion_end(command, start):
    """The end of the parameter expansion at `start`, or None if there is none.

    **The special parameters are expansions too**, and a scan that accepted
    only `[A-Za-z0-9_]` never saw them: `$@`, `$*` and `$!` are empty in the
    shell Claude Code runs commands in — no positional parameters, no
    background job — so `git $@push origin +HEAD:main` closes up into a force
    push, and `--out$@put=` and `ext$@::` reopen the other two checks the same
    way. Found by an adversarial audit; live on `main`.

    `$#`, `$?`, `$$`, `$-` and `$0` are deliberately absent: each expands to
    something non-empty, so none of them can join two words.
    """
    if not command.startswith("$", start):
        return None
    if command.startswith("${", start):
        close = _closing_brace(command, start + 2)
        return None if close is None else close + 1
    if command[start + 1:start + 2] in ("@", "*", "!"):
        return start + 2
    scan = start + 1
    while scan < len(command) and (command[scan].isalnum()
                                   or command[scan] == "_"):
        scan += 1
    return scan if scan > start + 1 else None


def glued(command, start, end):
    """Whether `command[start:end]` touches other characters of its own word.

    A word boundary is whitespace, a metacharacter, or the end of the string —
    so an expansion standing alone as `$BRANCH` is not glued, and the `${x}` of
    `--out${x}put=` is. This is the whole of the line between an expansion
    whose emptiness closes a word up and one that simply supplies a value.

    **A quote is NOT a boundary**, and counting one as such left half of this
    open: `git $x'push' origin +HEAD:main` runs the push, because quoting ends
    no word in bash — `'pu'$x'sh'` is one word too. Found by an adversarial
    audit after the `${x}` half had been closed, which is this file's own
    lesson about fixing the case in front of you rather than the grammar
    behind it.
    """
    def boundary(position):
        if position < 0 or position >= len(command):
            return True
        return command[position] in METACHARACTERS

    return not (boundary(start - 1) and boundary(end))


def without_substitutions(command):
    """`command` with every command substitution deleted rather than tokenised.

    **A substitution that prints nothing leaves the words around it joined**,
    and that is quote removal rather than run-time content: the dangerous
    string is literally in the source. `git $( )push origin +HEAD:main` runs
    the push — measured — while `shlex(punctuation_chars=True)` emitted `(` and
    `)` as their own tokens, `command_runs` ended the run there, and the second
    run held no `git` token for `git_segments` to find. The same shape hid
    `--out$( )put=` and `ext$( )::`, so it reopened all three checks at once.
    Raised in an adversarial audit; verified allowed, on `main` as well.

    `word_end` already implements exactly this rule — a substitution is part of
    the word it sits in — but only for a redirect target. Judging this string
    **beside** the ordinary one is the general form: one reading is what bash
    does when the substitution prints something, the other is what it does when
    it prints nothing, and both have to be safe.

    **A parameter expansion is deleted only where it is GLUED into a word**,
    and the line between the two cases is the one the paragraph above draws.
    `git ${x}push origin +HEAD:main` and `git log --out${x}put=/tmp/probe` run
    exactly as their `$( )` spellings do — the dangerous string is literally in
    the source and only an empty expansion is needed to close the word up. But
    `git push origin $BRANCH` is traffic this repository writes, and deleting a
    WHOLE word would refuse an honest push for naming no destination. So the
    test is adjacency: an expansion touching other characters of its own word
    goes, one standing alone stays. Raised in an adversarial audit, which
    pointed out that the residual named in `docs/harness-boundaries.md` is
    about a value assembled at run time — `F=--output=x; git log $F` — and that
    this is not that.
    """
    # One model of bash's quoting, shared: this scan used to keep its
    # own, which never learned that `$'…'` takes escapes. See
    # `quote_states`.
    states = quote_states(command)
    out, index = [], 0
    while index < len(command):
        char = command[index]
        if states[index] == "single":
            out.append(char)
            index += 1
            continue
        if char == "\\" and index + 1 < len(command):
            out.append(char)
            out.append(command[index + 1])
            index += 2
            continue
        if command.startswith("$(", index):
            close = _closing_paren(command, index + 2)
            if close is None:
                break
            index = close + 1
            continue
        if char == "`":
            close = index + 1
            while close < len(command):
                if command[close] == "\\" and close + 1 < len(command):
                    close += 2
                    continue
                if command[close] == "`":
                    break
                close += 1
            if close >= len(command):
                break
            index = close + 1
            continue
        if char == "$" and command[index + 1:index + 2] not in ("'", '"'):
            end = expansion_end(command, index)
            if end is None and command.startswith("${", index):
                # **An unbalanced `${` must END the scan, the way `$(` and a
                # backtick already do.** Advancing one character and rescanning
                # from the next `${` is quadratic: `"${" * 20000` took the hook
                # past its 60-second timeout, and a hook that produces no
                # output in time is non-blocking — fail-open by exhaustion
                # rather than by misreading. Found by an adversarial audit.
                break
            if end is not None and glued(command, index, end):
                index = end
                continue
        out.append(char)
        index += 1
    return "".join(out) + command[index:] if index < len(command) else "".join(out)


def outside_verbatim(command, reading):
    """`reading` applied to `command` except inside a NON-expanding body.

    **A quoted heredoc body expands nothing**, so rewriting one is inventing
    text the shell will never produce. The readings were run over the raw
    command, and a body line reading `${x:-EOF}` was rewritten into an early
    terminator — after which the rest of an innocent filing was read as
    commands and refused. Raised in review; measured.

    An expanding body is left to the reading, because bash does expand there.
    """
    spans = [(start, end) for start, end, expands in heredoc_spans(command)
             if not expands]
    if not spans:
        return reading(command)
    out, cursor = [], 0
    for start, end in spans:
        out.append(reading(command[cursor:start]))
        out.append(command[start:end])
        cursor = end
    out.append(reading(command[cursor:]))
    return "".join(out)


def rewriting_expansions(command, replace):
    """`command` with each parameter expansion put through `replace`.

    `replace(text)` is given the expansion as written and returns what to put
    in its place, or None to leave it alone. Single-quoted regions are left
    untouched, because a `$` is literal there.
    """
    # One model of bash's quoting, shared: this scan used to keep its
    # own, which never learned that `$'…'` takes escapes. See
    # `quote_states`.
    states = quote_states(command)
    out, index = [], 0
    while index < len(command):
        char = command[index]
        if states[index] == "single":
            out.append(char)
            index += 1
            continue
        if char == "\\" and index + 1 < len(command):
            out.append(char)
            out.append(command[index + 1])
            index += 2
            continue
        if char == "$" and command[index + 1:index + 2] not in ("'", '"'):
            end = expansion_end(command, index)
            if end is None and command.startswith("${", index):
                break
            if end is not None:
                written = replace(command[index:end])
                out.append(command[index:end] if written is None else written)
                index = end
                continue
        out.append(char)
        index += 1
    return "".join(out)


def splitting_expansions(command):
    """`command` with every parameter expansion read as WHITESPACE.

    **An expansion can split one word into several, and nothing here modelled
    that.** The whole expansion model was "an empty one joins its neighbours";
    the converse is `${IFS}`, which holds a space by default, so
    `git push${IFS}origin +HEAD:main` is the entire force push written as one
    `shlex` token. Found by an adversarial audit; live on `main`.

    Read beside the other readings rather than instead of them: an expansion is
    empty, or whitespace, or its own default text, and the command is only safe
    if it is safe under all of them.
    """
    return rewriting_expansions(command, lambda _text: " ")


# `${name:-word}` and its family. The operator decides when the default is
# used; every one of them can put `word` on the command line.
DEFAULTED = re.compile(r"^\$\{[^{}:=?+-]*(?::?[-=?+])(?P<word>.*)\}$", re.DOTALL)


def defaulted_expansions(command):
    """`command` with every `${name:-word}` read as its `word`.

    **This is not the residual the documentation already names.** That one is a
    value assembled at run time — `F=--output=x; git log $F` — which no hook is
    given. Here the dangerous text is literally in the source and an unset
    variable is the default state of the shell, so `git ${x:-push} origin
    +HEAD:main` is a force push written in plain sight. Found by an adversarial
    audit; live on `main`.
    """
    def written(text):
        match = DEFAULTED.match(text)
        return None if match is None else match.group("word")

    return rewriting_expansions(command, written)


# A brace expansion that yields exactly one word is pure obfuscation of the
# text inside it, and `{`/`}` are in neither `METACHARACTERS` nor
# `PUNCTUATION`, so `p{u..u}sh` survived as one opaque token.
BRACE = re.compile(r"\{(?P<from>[^{}.,\s]+)(?:\.\.(?P<to>[^{}.,\s]+)|,(?P<rest>[^{}]*))\}")


def brace_expanded(command):
    """`command` with each brace expansion read as its first alternative.

    A single-element range — `p{u..u}sh` — is exactly `push` to bash, and a
    list takes its first word, which is the reading that hides a literal.
    Found by an adversarial audit; live on `main`.
    """
    def written(match):
        if match.group("to") is not None:
            return match.group("from") if match.group("to") == match.group("from") else match.group(0)
        return match.group("from")

    return BRACE.sub(written, command)


def dollar_quotes(command):
    """Every `$'…'` and `$"…"` in `command`, as `(start, end, ansi_c)`.

    **These are QUOTING FORMS and `shlex` has no rule for either**, so the `$`
    stayed glued outside the quote and the token was `$git` rather than `git`.
    `program_name` then matched nothing, `git_segments` yielded no segment at
    all, and every check that lives inside that loop — the push allow-list, the
    forbidden flags, `ext::` — was skipped at once. Measured on bash 5.2.26:
    `$'git' push origin +HEAD:main`, `$"git" …`, `$'g'it …` and
    `git p$'ush' …` all run the push, and all were admitted here and on `main`.
    Raised in an adversarial audit.

    `end` is just past the closing quote, and `ansi_c` says which form it is,
    because only `$'…'` decodes escapes.
    """
    # One model of bash's quoting, shared: this scan used to keep its
    # own, which never learned that `$'…'` takes escapes. See
    # `quote_states`.
    states = quote_states(command)
    found, index = [], 0
    while index < len(command):
        char = command[index]
        if states[index] in ("single", "double"):
            index += 1
            continue
        if char == "\\" and index + 1 < len(command):
            index += 2
            continue
        if (char == "$"
                and command[index + 1:index + 2] in ("'", '"')):
            # **Neither form is a quoting form INSIDE double quotes**, and
            # missing that broke this three ways at once. To bash
            # `"regex $'\\d' matches"` is an ordinary message about a regex —
            # it was refused. `"$'\\x22'"` was decoded and re-emitted as a
            # single-quoted word *inside* the surrounding double quotes, which
            # unbalanced the line, sent it to the `ValueError` path and let
            # `git p''ush origin +HEAD:main` through beside it. And `"a$"`
            # closed at the wrong quote, swallowing the rest of the line into
            # one word. All three raised in an adversarial audit; all three
            # this branch's own doing.
            #
            # **Escape-aware, like every other closer in this file.** A plain
            # `find` closed `$"\"'"` on the ESCAPED quote, resumed inside the
            # string, read the `'` there as opening single quotes, and from
            # then on saw nothing — so a later `$'push'` was never un-sigilled
            # and `git $'push' origin +HEAD:main` was admitted. That is the
            # `$'\''` desync of the round before, in the sibling form. Raised
            # in an adversarial audit.
            quote = command[index + 1]
            close = index + 2
            while close < len(command):
                if command[close] == "\\" and close + 1 < len(command):
                    close += 2
                    continue
                if command[close] == quote:
                    break
                close += 1
            if close >= len(command):
                break
            found.append((index, close + 1, quote == "'"))
            index = close + 1
            continue
        index += 1
    return found


ANSI_C_SIMPLE = {
    "a": "\a", "b": "\b", "e": "\x1b", "E": "\x1b", "f": "\f", "n": "\n",
    "r": "\r", "t": "\t", "v": "\v", "\\": "\\", "'": "'", '"': '"', "?": "?",
}


def decode_ansi_c(body):
    """The text `$'<body>'` names, or None where an escape is not decodable.

    **Refusing every escape was safe and cost too much.** The first form of
    this refused any `$'…'` carrying a backslash, which took `echo $'\\n'`,
    `printf $'\\t'` and `grep -n $'\\t' file.txt` with it — ordinary traffic
    that has nothing to do with git, refused by a git guard. Raised in an
    adversarial audit.

    Decoding instead is safe **because the list only decides how much honest
    traffic is admitted, never whether a bypass gets through**: an escape this
    does not know returns None and the command is refused, so a gap costs a
    false positive rather than a force push. That is the opposite direction
    from the deny-lists this file refuses elsewhere, and it is why a list is
    affordable here.
    """
    out, index = [], 0
    while index < len(body):
        char = body[index]
        if char != "\\":
            out.append(char)
            index += 1
            continue
        if index + 1 >= len(body):
            return None
        escape = body[index + 1]
        if escape in ANSI_C_SIMPLE:
            out.append(ANSI_C_SIMPLE[escape])
            index += 2
            continue
        if escape in "01234567":
            # **`\\0nnn` counts its three digits AFTER the zero**, and reading
            # the zero as one of them made `$\'\\0165\'` the two characters
            # `\x0e5` where bash gives `u` — so `git p$\'\\0165\'sh origin
            # +HEAD:main` was a push the guard could not see. Raised in review;
            # verified allowed. The bare `\\nnn` form keeps its own count.
            first = index + 2 if escape == "0" else index + 1
            digits = body[first:first + 3]
            while digits and not all(d in "01234567" for d in digits):
                digits = digits[:-1]
            out.append(chr(int(digits, 8) & 0xFF) if digits else "\0")
            index += (first - index) + len(digits)
            continue
        if escape in "xuU":
            width = {"x": 2, "u": 4, "U": 8}[escape]
            digits = body[index + 2:index + 2 + width]
            while digits and not all(d in "0123456789abcdefABCDEF" for d in digits):
                digits = digits[:-1]
            if not digits:
                return None
            # **`chr` raises above 0x10FFFF, and a hook that raises fails
            # OPEN.** `$'\\UFFFFFFFF'` took the process down with an
            # `OverflowError`: exit 1, empty stdout, which `PreToolUse` treats
            # as a non-blocking error, so the command ran. Found by an
            # adversarial audit, and it is the worst shape a defect in this
            # file can take — every refusal in it is reached by returning a
            # string, and none of that happens after a traceback.
            point = int(digits, 16)
            if point > 0x10FFFF:
                return None
            out.append(chr(point))
            index += 2 + len(digits)
            continue
        if escape == "c":
            if index + 2 >= len(body):
                return None
            # **`str.upper()` is not length-preserving in Unicode, and `ord`
            # raises on what it returns.** `ß` upper-cases to `SS`, and
            # `$'\cß'` took the hook down with a `TypeError` — exit 1, empty
            # stdout, which `PreToolUse` treats as non-blocking, so the command
            # ran. `ﬁ`, `ŉ`, `ǰ`, `ΐ`, `ẖ` and `ẚ` do the same. Found by an
            # adversarial audit; a regression against `main`, introduced with
            # the decoder, and the second crash this file has had from
            # assuming a character-wise operation stays one character.
            control = body[index + 2]
            folded = control.upper()
            if len(folded) != 1:
                return None
            out.append(chr(ord(folded) ^ 0x40))
            index += 3
            continue
        return None
    # **A NUL truncates the word in bash, and keeping one changed what the
    # word said.** `$'a\\0b'` is the single byte `a`, so `git p$'\\0'ush` is
    # `git push` — measured — and the hook was holding a NUL in the middle of a
    # token nothing would match. Truncating models the shell exactly, where
    # refusing would have been the cruder answer. Found by an adversarial
    # audit.
    text = "".join(out)
    return text.split("\0", 1)[0]


def single_quoted(text):
    """`text` as a single-quoted shell word, whatever it contains."""
    return "'" + text.replace("'", "'\"'\"'") + "'"


def unreadable_dollar_quote(command):
    """Why `command`'s `$'…'` or `$"…"` cannot be read, or None.

    **Two different reasons, and one sentence for both said the wrong thing.**
    A plain `$"safe"` carries no escape at all; it is refused because its
    translation is a lookup in a catalogue this hook is not given. Reporting
    that as an undecodable escape tells a caller to go looking for one, in a
    command that has none. Raised in review.
    """
    for _start, _end, ansi_c in dollar_quotes(command):
        if not ansi_c:
            return (
                "a `$\"…\"` is a translated string, so what the word says is "
                "decided by a message catalogue this guard is not given; "
                "refusing rather than reading the source as if it were the "
                "result."
            )
    if undecodable_dollar_quote(command):
        return (
            "a `$'…'` carries an escape this guard does not decode, so it "
            "cannot tell what the word says; refusing rather than reading "
            "part of it."
        )
    return None


def undecodable_dollar_quote(command):
    """Whether a `$'…'` or `$"…"` in `command` carries an escape to decode.

    The same decision `undecodable_heredoc` records, one construct along, and
    for the same reason: decoding every escape bash supports is a list that
    trails bash's, and each gap in one reopens the hole it was written to
    close. `$'\\''` is the shape that forces the question — it is a single
    quote produced by an escape, which desynchronised `substitutions` and sent
    the whole command down the `ValueError` path, where the push allow-list
    does not run.

    **Both forms can fail, and the locale one always does.** `$'…'` can carry
    an escape outside the set `decode_ansi_c` knows, so it fails when it does.
    `$"…"` fails unconditionally, and the word *translated* is why.

    **The translation is a lookup in a catalogue this hook is not given**, and
    the first version of this paragraph named the wrong half of the problem. It
    said `$"…"` is a translated double-quoted string and then refused only the
    expansions inside it — as though `$"safe"` were the word `safe` once no
    substitution was present. It is not: bash resolves `$"…"` through gettext
    against `TEXTDOMAIN` and `TEXTDOMAINDIR`, both ordinary environment
    variables, so a catalogue placed in the checkout decides what the word
    says. Measured with a hand-built `.mo`: `$"safe"` printed `printf`, and in
    command position `$"safe" RAN` **executed** it. The same lookup can return
    `git`. Raised in review.

    So this is the residual `docs/harness-boundaries.md` names — text the shell
    is *told* rather than text it is given — arriving in a construct a caller
    can type literally, and the answer is the one that file already states for
    a script on disk: what cannot be read is not judged, and what is not judged
    is refused. The cost is every `$"…"`, which nothing in this repository
    writes.
    """
    for start, end, ansi_c in dollar_quotes(command):
        if not ansi_c:
            return True
        if decode_ansi_c(command[start + 2:end - 1]) is None:
            return True
    return False


def strip_dollar_quotes(command):
    """`command` with every `$'…'` and `$"…"` replaced by what it names.

    `shlex` has no rule for either form, so the `$` stayed glued outside the
    quote and `$'git'` tokenised as `$git` — which `program_name` did not match,
    so `git_segments` yielded nothing and the push allow-list, the forbidden
    flags and `ext::` were all skipped at once.

    The escapes are decoded rather than dropped, so `$'\\x67it'` becomes `git`
    and is judged as one. A body this file cannot read is refused before this
    runs — which is every locale-quoted one, and an ANSI-C one carrying an
    escape outside the decoded set — so neither the `None` case nor the
    translated form can arrive here.
    """
    out, cursor = [], 0
    for start, end, ansi_c in dollar_quotes(command):
        if not ansi_c:
            continue
        text = decode_ansi_c(command[start + 2:end - 1])
        if text is None:
            continue
        out.append(command[cursor:start])
        out.append(single_quoted(text))
        cursor = end
    out.append(command[cursor:])
    return "".join(out)


def join_continuations(command, quotes=True):
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

    **`quotes` is false for a heredoc BODY, where a quote is an ordinary
    character and the continuation goes anyway.** An expanding body removes
    `\\<newline>` before it expands, so
    `git commit -F - <<EOF` / `$\\<newline>(git push origin +HEAD:main)` / `EOF`
    forms a live `$(…)` and runs the push — while this function, tracking
    quotes that are not quotes, could reach the wrong conclusion about where
    the escape sits. Raised in review; verified allowed.
    """
    # One model of bash's quoting, shared: this scan used to keep its
    # own, which never learned that `$'…'` takes escapes. See
    # `quote_states`.
    states = quote_states(command, quotes=quotes)
    out, index = [], 0
    while index < len(command):
        char = command[index]
        if states[index] == "single":
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


def word_end(command, position, ordinary):
    """The end of the shell WORD beginning at `position`.

    **One parse of a word, because there were two and they disagreed.**
    `stdin_scripts` had its own, ending a here-string at the first
    unquoted metacharacter — so `bash <<<$(printf 'git push origin
    +HEAD:main')` yielded `$` as the script, the inner `printf` was judged
    as the data it is, and the redirection strip removed the rest. The push
    ran and the hook admitted it. Raised in review; verified allowed, with
    the backtick spelling beside it — which is this parse's OWN fail-open,
    recorded below, arriving a second time in the function that did not
    share it.

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
    def plain(offset):
        return offset < len(command) and ordinary[offset]

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
            end = word_end(command, end, ordinary)
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
        end = word_end(command, end, ordinary)
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
    # One model of bash's quoting, shared: this scan used to keep its own,
    # which never learned that `$'…'` takes escapes — so `: $'x\\''; git log
    # "$(git push origin +HEAD:main)"` ran the nested push while this state
    # machine closed at the escaped quote, reopened at the real closer, and
    # never saw the `$(`. Raised in review; verified allowed. See
    # `quote_states`.
    #
    # **`$(` is live inside DOUBLE quotes**, which is the whole shape of the
    # bypass this pass exists for, so only the single-quoted state stops it —
    # and the one branch below that DOES need the double-quoted state reads
    # it from the same list rather than tracking a second thing.
    states = quote_states(command, quotes=quotes)
    while index < len(command):
        char = command[index]
        if states[index] == "single":
            # **An apostrophe inside double quotes opens nothing**, and reading
            # one as a quote suppressed every substitution after it:
            # `git log "don't $(git push origin +HEAD:main)"` runs the push, and
            # the scanner entered single-quote state at `don't`, never saw the
            # `$(`, and handed `shlex` an opaque quoted argument. Raised in
            # review; verified allowed, on `main` as well — and settled here by
            # asking `quote_states` rather than by tracking a second flag,
            # which is the same answer one layer up.
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
        if (quotes and states[index] != "double"
                and (command.startswith("<(", index)
                     or command.startswith(">(", index))):
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


# `NAME=value` before a command sets a variable for it and is not the command.
ASSIGNMENT = re.compile(r"^[A-Za-z_][A-Za-z0-9_]*=")


def leading_command(run):
    """The command word of `run`, past any assignment prefix.

    **`X=1 bash` is a run led by `bash`**, and reading the first token instead
    made it a run led by `X=1`: `X=1 bash <<<'git push origin +HEAD:main'` had
    its here-string stripped as an ordinary redirect target, the evaluator scan
    then saw a `bash` with no script, and the push ran. Raised in review;
    verified allowed.

    The same reading is owed to the printer half — `X=1 echo … | bash` — and to
    the data-only exemption, which is why this is one function rather than a
    test repeated at each site.
    """
    for token in run:
        if not ASSIGNMENT.match(token):
            return token
    return ""


def reads_stdin_as_script(words):
    """Whether a run made of `words` will EXECUTE what arrives on its stdin.

    **The test used to be that the run's LEADING word is a shell**, and a
    wrapper in front of one defeated it: `echo 'git push origin +HEAD:main' |
    command bash` runs the push, and so does the `env bash` spelling, while
    the leading word is `command` or `env` and no shell was found. Raised in
    review; both verified allowed, and `env` found beside the one that was
    reported.

    **So the shell is looked for anywhere in the run, and the exemption is the
    allow-list rather than the wrapper set.** Enumerating the wrappers that DO
    exec their argument is the direction `DATA_ONLY_COMMANDS` argues against in
    its own comment — it fails open on the first one nobody thought of, and
    `command`, `env`, `nohup`, `nice`, `stdbuf`, `setsid`, `timeout`, `ionice`
    and `chrt` are nine before anyone has looked hard. Reading any shell name
    in the run costs an over-refusal instead, and it costs one only in the
    shape `echo '…git push…' | grep bash`, because the printer half of the
    pipeline pass has to match first.

    A run carrying `-c` reads its script from the argv rather than from stdin,
    and `evaluated_scripts` judges that channel at any position already.
    """
    body = [word for word in words if not ASSIGNMENT.match(word)]
    if not body or program_name(body[0]) in DATA_ONLY_COMMANDS:
        return False
    for position, word in enumerate(body):
        if program_name(word) not in EVALUATORS:
            continue
        # **A `-c` before the shell is the WRAPPER's option**, and reading the
        # whole run for one confused the two: `ionice -c 2 bash` runs bash on
        # its stdin, `-c` there being the scheduling class, and the run was
        # dismissed as carrying its own script. Raised in review; verified
        # allowed. Only what follows the shell token can be the shell's
        # script flag.
        if not any(SCRIPT_FLAG.match(element) for element in body[position + 1:]):
            return True
    return False


def _run_words(command, start, end, ordinary):
    """`command[start:end]` split into words on its unquoted metacharacters."""
    words, index = [], start
    while index < end:
        while index < end and command[index] in " 	":
            index += 1
        cursor = index
        while cursor < end and not (
                ordinary[cursor] and command[cursor] in METACHARACTERS):
            cursor += 1
        if cursor > index:
            words.append(command[index:cursor])
        index = cursor if cursor > index else index + 1
    return words


def _run_bounds(command, position, ordinary):
    """The half-open span of the command run containing `position`."""
    start = position
    while start > 0 and not (
            ordinary[start - 1] and command[start - 1] in RUN_SEPARATORS):
        start -= 1
    end = position
    while end < len(command) and not (
            ordinary[end] and command[end] in RUN_SEPARATORS):
        end += 1
    return start, end


def forwards_to_evaluator(command, position, ordinary):
    """Whether the run at `position` writes into a shell later in its pipeline.

    **A heredoc belongs to the run that opens it and its BYTES belong to
    whatever is downstream of the pipe.** `cat <<'EOF' | bash` with a push in
    the body runs it: the opener is `cat`'s, so `stdin_scripts` yielded
    nothing, and `strip_heredocs` then removed the body — the only copy of the
    script — before anything else could look. The here-string spelling
    `cat <<<'git push origin +HEAD:main' | bash` fails the same way. Raised in
    review; both verified allowed, and both live on `main`.

    Only a `|` carries stdout onward, so `||` ends the walk rather than
    continuing it, and a `)` between the run and the pipe is stepped over
    because a subshell writes into the pipe exactly as a bare run does.
    """
    _, index = _run_bounds(command, position, ordinary)
    while index < len(command):
        while index < len(command) and (
                command[index] in " 	"
                or (ordinary[index] and command[index] == ")")):
            index += 1
        if not (index < len(command) and ordinary[index]
                and command[index] == "|") or command.startswith("||", index):
            return False
        index += 2 if command.startswith("|&", index) else 1
        start = index
        while index < len(command) and not (
                ordinary[index] and command[index] in RUN_SEPARATORS):
            index += 1
        if reads_stdin_as_script(_run_words(command, start, index, ordinary)):
            return True
    return False


def _consumes_as_script(command, position, ordinary):
    """Whether the script at `position` is executed by its own run or a later
    one in the same pipeline."""
    start, end = _run_bounds(command, position, ordinary)
    return (reads_stdin_as_script(_run_words(command, start, end, ordinary))
            or forwards_to_evaluator(command, position, ordinary))


def pipeline_groups(tokens):
    """`tokens` split into pipelines, each a list of the runs it joins.

    **A pipe is not an adjacency**, and comparing neighbouring runs let an
    intermediate stage carry the bytes past the check:
    `printf 'git p%ssh origin +HEAD:main' u | cat | bash` pairs as
    printf-then-cat and cat-then-bash, and neither pair is a printer feeding a
    shell — while the shell still runs what the printer wrote. Raised in
    review; verified allowed.

    `command_runs` drops the boundary that separated two runs, which is what
    made the distinction unavailable; this keeps it just long enough to say
    whether the runs are in the same pipeline.
    """
    groups, current, run = [], [], []
    for token in tokens:
        if not is_boundary(token):
            run.append(token)
            continue
        if run:
            current.append(run)
            run = []
        if token in ("|", "|&"):
            continue
        if current:
            groups.append(current)
            current = []
    if run:
        current.append(run)
    if current:
        groups.append(current)
    return groups


def unmodelled_printer(tokens):
    """Whether a printer whose OUTPUT this file cannot reproduce feeds a shell.

    **Joining a printer's argv is not the bytes it writes**, and where the two
    differ the join is the safe-looking one. `printf 'git p%ssh origin
    +HEAD:main' u | bash` runs the push; the join is `git p%ssh origin
    +HEAD:main u`, which every check reads as harmless. `echo -e` does the same
    through its escapes. Raised in review; both verified allowed.

    Reproducing `printf` is a specification this file will not carry — the same
    reason it refuses to enumerate git's executing config keys — so the
    unmodellable case refuses instead. The plain forms still go through
    `evaluated_scripts`, which judges the literal text, so `echo 'git status'
    | bash` is unaffected.
    """
    for group in pipeline_groups(tokens):
        shells = [position for position, run in enumerate(group)
                  if reads_stdin_as_script(run)]
        if not shells:
            continue
        for run in group[:max(shells)]:
            # **A run can be assignments and nothing else**, and `leading_command`
            # answers `""` for one — which `list.index` does not find, so
            # `X=1 | bash` raised `ValueError` out of the hook. A crash is
            # empty stdout, which `PreToolUse` treats as non-blocking: this
            # was a fail-open on a shape a caller can type. Raised in review;
            # verified as a crash.
            command_word = leading_command(run)
            if not command_word:
                continue
            name = program_name(command_word)
            arguments = run[run.index(command_word) + 1:]
            if name == "printf" and any("%" in element for element in arguments):
                return True
            if name == "echo" and any(element.startswith("-") and "e" in element
                                      for element in arguments):
                return True
    return False


def stdin_scripts(command):
    """Every script a shell in `command` is handed on its STDIN.

    **`evaluated_scripts` models one channel by which a shell receives a
    script, and bash has three.** It reads the argv element after `-c`; a shell
    also runs what arrives on stdin, and both spellings of that put the script
    text in the command string where a hook can read it:

        bash <<<'git push origin +HEAD:main'
        bash <<EOF
        git push origin +HEAD:main
        EOF

    Both ran the push and both were admitted — on `main` as well. Found by an
    adversarial audit, which generated 3,696 obfuscations, took the 919 the
    guard allowed, ran each under a shimmed bash and found 431 that executed
    the push.

    **These are not the residual that file's docstring names.** That one is
    `bash script.sh`, a file the hook is not given, and the computed shape
    `sh -c "$(echo …)"`. Here nothing is computed and nothing is on disk: the
    script is a literal word in the argv, exactly as it is in `bash -c '…'` —
    which this guard already refuses. The two halves disagreed, and this is the
    half that was wrong.

    Every other reader of these constructs is left alone, which is what keeps
    `git commit -F - <<EOF` a filing rather than a command: the leading word of
    the run has to be a shell.
    """
    # The bodies first, for `undecodable_heredoc`'s reason: an apostrophe in
    # one used to leave this scan in quote state for everything after it.
    spans = heredoc_spans(command)
    ordinary = [False] * len(command)
    literal = [False] * len(command)
    escaped = None
    for index, in_quotes, in_comment in shell_positions(
            command, [(start, end) for start, end, _ in spans]):
        ordinary[index] = not in_quotes and not in_comment
        if index == escaped:
            # **An escaped metacharacter is part of the word**, and treating
            # one as a boundary cut the script short: the here-string of
            # `bash <<<git\\ push\\ origin\\ +HEAD:main` yielded `git\\`
            # alone, while the redirection strip removed the whole thing, so
            # nothing downstream saw the push. Raised in review.
            literal[index] = True
            escaped = None
            continue
        if ordinary[index] and command[index] == "\\":
            literal[index] = True
            escaped = index + 1

    def boundary(position):
        return (ordinary[position] and not literal[position]
                and command[position] in METACHARACTERS)

    # **Bodies belong to introducers in ORDER, and `rfind` gave every body the
    # last introducer before it.** In `bash <<A; cat <<B` the first body is
    # `bash`'s, and `rfind` found `<<B`, decided the reader was `cat`, and
    # never judged the script bash runs. Raised in review; verified allowed.
    #
    # `heredoc_spans` yields its spans in opener order and skips an opener that
    # sits inside an earlier body, so the pairing walks both lists together
    # rather than searching backwards from each body.
    openers = [
        match.start() for match in HEREDOC.finditer(command)
        if ordinary[match.start()]
        and not (match.start() > 0 and command[match.start() - 1] == "<")
        and not command.startswith("<<<", match.start())
    ]
    # **Two monotonic cursors, because the containment test that used to sit
    # here was quadratic.** It re-scanned every earlier body for every
    # opener/body pair, so a command carrying enough heredocs ran for long
    # enough to hit the hook timeout — which is empty stdout, which is
    # non-blocking. Raised in review, against the commit that had just removed
    # the same shape from `heredoc_spans` and pinned only that function's
    # timing. An opener inside a body is no longer in this list at all: the
    # scan above is told where the bodies are, so it reports one as quoted.
    cursor = 0
    for start, end, _expands in spans:
        while cursor < len(openers) and openers[cursor] >= start:
            cursor += 1
        if cursor >= len(openers):
            break
        opener = openers[cursor]
        cursor += 1
        if _consumes_as_script(command, opener, ordinary):
            yield command[start:end]

    index = 0
    while index < len(command):
        if not (ordinary[index] and command.startswith("<<<", index)):
            index += 1
            continue
        consumed = _consumes_as_script(command, index, ordinary)
        cursor = index + 3
        while cursor < len(command) and command[cursor] in " \t":
            cursor += 1
        word = word_end(command, cursor, [not literal[position] and value
                                          for position, value
                                          in enumerate(ordinary)])
        if consumed:
            # **A here-string is quote-removed before the shell runs it, and
            # `shlex` has no rule for either dollar quote.** So
            # `bash <<<$'git push origin +HEAD:main'` handed the recursion
            # `$git push …` — a name `program_name` does not match — while bash
            # ran the push. `$'' + BS + BS + 'x67it …'` and the locale spelling did the
            # same. Raised in review; verified allowed.
            #
            # An undecodable one is yielded whole rather than normalised: the
            # recursive judge applies the same fail-closed check to it and
            # refuses with the reason that check states, which keeps one
            # sentence for one decision.
            text = command[cursor:word]
            if undecodable_dollar_quote(text):
                yield text
                index = max(word, index + 3)
                continue
            try:
                parts = shlex.split(strip_dollar_quotes(text), posix=True)
            except ValueError:
                parts = [text]
            if parts:
                yield " ".join(parts)
        index = max(word, index + 3)


def substitution_fed_shells(command):
    """Whether a process substitution supplies a shell in `command` its script.

    **`bash < <(printf '%s\\n' 'git push origin +HEAD:main')` runs the push,
    and every pass here judged the halves apart.** The inner `printf` is data,
    correctly; the redirection strip then removes `< <(…)` whole, correctly,
    because a process substitution IS the redirect target; and what is left is
    a `bash` with no script, which is nothing at all. Raised in review;
    verified allowed, and `bash <(echo …)` runs it too, the substitution being
    a filename the shell is told to execute.

    **Refused rather than read, on `unmodelled_printer`'s argument.** What the
    shell executes is the substitution's OUTPUT, and reproducing a command's
    output is the specification this file declines to carry — the same reason
    `printf 'git p%ssh …' u | bash` refuses instead of being modelled. Reading
    the inner command instead would be right for `<(echo '…')` and wrong for
    every spelling that computes, and the wrong half fails open.

    A run led by a printer is left alone, exactly as the pipeline pass leaves
    one: `echo <(git push origin +HEAD:main)` is text, and the inner command is
    judged in its own right by `substitutions`.
    """
    ordinary = [False] * len(command)
    escaped = None
    bodies = [(start, end) for start, end, _ in heredoc_spans(command)]
    for index, in_quotes, in_comment in shell_positions(command, bodies):
        if index == escaped:
            escaped = None
            continue
        if command[index] == "\\" and not in_quotes and not in_comment:
            escaped = index + 1
            continue
        ordinary[index] = not in_quotes and not in_comment

    for index in range(len(command) - 1):
        if not (ordinary[index] and ordinary[index + 1]):
            continue
        if command[index:index + 2] not in ("<(", ">("):
            continue
        start, end = _run_bounds(command, index, ordinary)
        if reads_stdin_as_script(_run_words(command, start, end, ordinary)):
            return True
    return False


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

    **That bound was stated correctly and applied too widely.** `-c` is one of
    three channels a shell takes a script through, and the other two —
    `bash <<<'…'` and a heredoc — put the text in the command string, where it
    is as readable as the argument to `-c` this function already judges.
    `stdin_scripts` covers them; the file half is what remains outside.
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

    # **A shell with no script of its own reads one from the pipe**, and the
    # run before it is where that text is written: `echo 'git push origin
    # +HEAD:main' | bash` ran the push and was admitted. The printer is exempt
    # from the scan by `DATA_ONLY_COMMANDS` — correctly, its arguments are text
    # — but they stop being text the moment a shell is on the other end of the
    # pipe. Found by an adversarial audit.
    for group in pipeline_groups(tokens):
        shells = [position for position, run in enumerate(group)
                  if reads_stdin_as_script(run)]
        if not shells:
            continue
        for before in group[:max(shells)]:
            command_word = leading_command(before)
            if program_name(command_word) not in DATA_ONLY_COMMANDS:
                continue
            # Sliced past the command WORD rather than past the first token:
            # with an assignment prefix the two differ, and taking `before[1:]`
            # handed the judgement a string beginning `echo`, which the
            # data-only exemption then waved through.
            spoken = before[before.index(command_word) + 1:]
            written = [element for element in spoken
                       if not element.startswith("-")]
            if written:
                yield " ".join(written)


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

# What ends a command RUN. A subset of METACHARACTERS: a redirection
# operator and a space separate words within one run rather than ending
# it.
RUN_SEPARATORS = set(";&|()\n")


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


def offence(command, depth=0, judged=None):
    """The reason to refuse `command`, or None to allow it.

    **`judged` is a verdict cache, and it is what keeps the cost finite.** Each
    of the four readings and each extracted substitution recurses onto a string
    barely shorter than the one it came from, so a command nesting them
    multiplies: `$( echo ${a:-{z,X}} )` repeated seven times is 155 characters
    and took over sixty seconds — past the hook timeout, which produces no
    verdict, which `PreToolUse` treats as non-blocking. Fail-open by
    exhaustion, on an innocent command, and a regression from the commit that
    added the readings. Found by an adversarial audit.

    **The cache holds the verdict rather than the visit**, which is the part
    that has to be right: remembering only that a string had been seen would
    return None the second time a refusing string appeared, and lose the
    refusal. A string reached inside its own evaluation is recorded as None
    first, so a cycle terminates without inventing a verdict — the outer call
    is the one that answers.
    """
    if judged is None:
        judged = {}
    if command in judged:
        return judged[command]
    judged[command] = None

    verdict = _offence(command, depth, judged)
    judged[command] = verdict
    return verdict


def _offence(command, depth, judged):
    """`offence`'s body, called only through its cache."""
    if depth > MAX_NESTING:
        return (
            "this command nests shells or substitutions more deeply than the "
            "guard will follow; refusing rather than reading part of it."
        )

    if undecodable_heredoc(command):
        return (
            "a heredoc names a delimiter this guard cannot decode, so it "
            "cannot tell where the body ends or which lines after it are "
            "commands; refusing rather than reading part of it."
        )


    # **What bash does when a substitution prints nothing, judged beside what
    # it does when one prints something.** The words around an empty
    # substitution join, so `git $( )push origin +HEAD:main` is a push — and
    # the tokeniser saw `(` and `)` as run boundaries instead. Both readings
    # have to be safe, and only one of them is the string that was typed.
    # **An expansion has more than one reading, and the command is safe only if
    # it is safe under all of them.** Empty joins the words around it,
    # whitespace splits one into several, a default puts its own text on the
    # line, and a single-element brace range is the text inside it. Each is
    # what bash does in the shell these commands run in — no positional
    # parameters, no variables set — so none of these is the run-time residual
    # `docs/harness-boundaries.md` names; the dangerous string is in the source
    # in every case.
    for description, reading in (
        ("a command substitution taken as empty", without_substitutions),
        ("an expansion taken as whitespace", splitting_expansions),
        ("an expansion taken as its default", defaulted_expansions),
        ("a brace expansion taken as one word", brace_expanded),
    ):
        variant = outside_verbatim(command, reading)
        if variant != command:
            refusal = offence(variant, depth + 1, judged)
            if refusal is not None:
                return f"with {description}: {refusal}"

    if substitution_fed_shells(command):
        return (
            "a shell is handed its script by a process substitution, so what "
            "it runs is that command's output rather than anything written "
            "here; refusing rather than judging the source instead of the "
            "result."
        )

    for script in stdin_scripts(command):
        # **A substitution inside a script a shell will run supplies the
        # command itself**, and no reading here models that:
        # `bash <<<"$(printf git) push origin +HEAD:main"` runs the push, while
        # the inner `printf git` is judged as the data it is and the
        # empty-substitution reading leaves a bare `push …`. The same answer
        # `unmodelled_printer` gives, for the same reason — the text that
        # decides is not in the source. Raised in review; verified allowed.
        if substitutions(script):
            return (
                "a script handed to a shell on stdin builds part of itself "
                "with a command substitution, so what that shell runs cannot "
                "be read; refusing rather than judging the source instead of "
                "the result."
            )
        refusal = offence(script, depth + 1, judged)
        if refusal is not None:
            return f"in a script handed to a shell on stdin: {refusal}"

    for text, quotes in expandable_regions(command):
        # **The continuation join has to happen before anything looks for a
        # substitution, not only before the tokeniser.** Bash removes
        # `\<newline>` inside double quotes too, so
        # `git log "$\<newline>(git push origin +HEAD:main)"` becomes a live
        # `$(` — and this scan, running on the raw text, saw no opener while
        # `shlex` later returned the whole quoted value as data. Raised in
        # review; verified allowed. Only for a command-line region: a heredoc
        # body arrives with `quotes` false and is not a command line.
        text = join_continuations(text, quotes=quotes)
        for inner in substitutions(text, quotes=quotes):
            refusal = offence(inner, depth + 1, judged)
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
    resolved = strip_redirections(
        separate_lines(
            join_continuations(strip_comments(strip_heredocs(command)))))

    # **The check and the code that acts on it must read the SAME string**, and
    # putting this on the raw command was wrong twice over. It refused a
    # heredoc body or a comment that merely mentions `$'\n'` — data on every
    # path, which is the invariant the rest of this pipeline is built on, and
    # it made a commit message describing this very change unwritable. And it
    # missed `git $\<newline>'\x70ush' origin +HEAD:main`, where the sigil and
    # its quote are separated by a continuation: nothing was there to refuse on
    # the raw string, while `strip_dollar_quotes` — running after the join —
    # found the quote and un-sigilled it, leaving `shlex` a literal
    # `\x70ush` that is not `push`. Both raised in an adversarial audit; the
    # bypass was live on `main` too, the over-refusal was this branch's own.
    unreadable = unreadable_dollar_quote(resolved)
    if unreadable is not None:
        return unreadable

    # `strip_dollar_quotes` turns `$'…'` and `$"…"` into the ordinary quoting
    # `shlex` resolves, on the string just checked.
    stripped = strip_dollar_quotes(resolved)
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
        # **The push allow-list has to reach this path too, and it did not.**
        # The fallback scanned for forbidden flags and `ext::` alone, so any
        # command this guard cannot tokenise had the push grammar switched off
        # entirely — and a line is easy to make untokenisable on purpose. An
        # audit reached it through `$'\''`, whose escaped quote left `shlex`
        # with no closing quotation; the push then sat in plain text and was
        # admitted. Here the check can only be the crude one, which is the
        # point of the path.
        if re.search(r"\bgit\b[^;&|\n]*\bpush\b", stripped):
            return (
                "a `git push` appears in a command this guard could not "
                "tokenise, so its remote and refspec cannot be read; refusing "
                "rather than admitting a push nothing checked."
            )
        return None

    if unmodelled_printer(tokens):
        return (
            "a printer whose output this guard cannot reproduce writes into a "
            "shell, so what that shell runs cannot be read; refusing rather "
            "than judging the arguments instead of the bytes."
        )

    for script in evaluated_scripts(tokens):
        refusal = offence(script, depth + 1, judged)
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

    try:
        reason = offence(command)
    except Exception:  # noqa: BLE001 - the direction is the point
        # **A crash is empty stdout, and `PreToolUse` reads empty stdout as
        # non-blocking**, so every defect in this file has been a fail-open.
        # Four have been found by review and audit — a `ValueError` out of
        # `list.index`, two out of `str.index`, and one recursion — and each
        # admitted whatever the command was.
        #
        # **This is not the malformed-event case above and the two answers
        # differ on purpose.** An unreadable event says nothing about any
        # command, so refusing there would stop the session for a defect in
        # this file; a crash while judging THIS command says this command
        # broke the parser, and refusing one command is proportionate and
        # tells the caller exactly that.
        traceback.print_exc(file=sys.stderr)
        reason = (
            "this command crashed the guard that judges it, so nothing about "
            "it has been established; refusing rather than admitting what "
            "could not be read. The traceback is on stderr."
        )
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
