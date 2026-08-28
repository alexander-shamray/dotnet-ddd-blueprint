# Harness boundaries

**What the agent harness grants this repository's commands, what it refuses
them, and every place a grant is wider than the operation it was added for.**
This was `CLAUDE.md`'s *What cuts across them* section until it reached 582
lines. It is load-bearing whenever you touch `.claude/` — a command's
frontmatter, a helper script, `settings.json`, a hook, a subagent profile — and
inert for every other kind of change, which is what earns it a file rather than
a permanent seat in every session's context.

**The content is verbatim in its arguments**, on
[`pr-decision-log.md`](pr-decision-log.md)'s terms and for its reason: a summary
of an argument is how a rule gets "corrected" back. Four edits were made on the
way out, and naming them is cheaper than a claim that does not survive a grep.
The `### What cuts across them` heading became this file's title. One paragraph
arrived from `CLAUDE.md`'s *The commands* section — the one recording that
#150's suppression clause moved three grants along before it stuck — because
its subject is a grant and not a command; its opening was rebased from "That
last clause", which pointed at a paragraph that did not travel with it. And
**two self-references** were rebased the same way: the grant inventory's count
of rotted totals said "in this file" of `CLAUDE.md` and named two siblings that
now live in two other files, and the sandbox residual said the commands cite
"this file" — true while it sat in `CLAUDE.md`, and a contradiction of the
paragraph below the moment it moved. Not one argument was shortened, and no
paragraph was dropped.

**The second of those was found by review rather than by the move**, which is
worth recording where the edits are listed: a self-reference that stays *true
of its old home* reads correctly in isolation and only contradicts something
two screens away. Grep for "this file" after extracting prose, not just for
links.

**No headings were introduced.** The section is one long run of bold-led
paragraphs and it stays that way — inventing headings would mean deciding where
the topic boundaries are, which is an edit to the argument wearing a
navigation aid's clothes. Grep the bold lead-ins; they are the index.

**`/ship` and both sweeps cite `CLAUDE.md` as where the sandbox boundary and
its residuals are recorded, and those citations were deliberately left
alone.** `CLAUDE.md` keeps a *What cuts across them* section that forwards
here, so a command's reference lands one hop from what it names — where
rewriting nine command files to point at a new path would be nine chances to
get it wrong, in files the harness denies this session an edit to anyway. **A
new residual is stated here**, and `CLAUDE.md` carries the pointer rather than
a second copy.

**One of those citations is named rather than left to the general clause,
because it claims more than the others do.** `ship.md`'s grants callout says
`CLAUDE.md` "keeps the count and the inventory" — a possessive claim, where the
other nine only say a thing is *recorded* there. The inventory half resolves in
one hop like the rest: `CLAUDE.md` forwards, and the numbered inventory is
below. **The count half was already stale before this branch touched
anything** — the headline figure was deliberately dropped after it read "six"
and #56 closed the fourth, and the paragraph below says so — so a reader
following that word finds nothing, and found nothing on `main` either. It is
recorded here rather than fixed in place for the reason the clause above
gives, and named here rather than covered silently: **a residual that does not
mention the sentence it is covering is the same shape as an unstated one.**

> **A grant is not a whitelist, and that is the trap under everything below.**
> If you read one paragraph here, read the one beginning *A grant is not a
> whitelist* — `allowed-tools` is an auto-approval list, so omitting a tool from
> a command's frontmatter withholds nothing. Refusing a capability takes a
> **deny**.

> **This file is outside the blueprint tree, so nothing structural catches its
> drift.** It is in no index and behind no nav footer, exactly like
> `roadmap.md`, `testing.md`, `lessons.md` and `pr-decision-log.md`.
> `/check-links` does not reach it and `/validate-blueprint` does not name it.
> The one rule in `CLAUDE.md` covers it, and that is all that does.

---

**File permission rules take `Edit(...)`, never `Write(...)`.** `Edit(path)`
covers every file-editing tool, `Write` included; a `Write(path)` rule matches
nothing and Claude Code **refuses to start** until it is removed:

```
Permission deny rule (.claude\settings.json): Write(.remember/**) is not matched
by file permission checks — only Edit(path) rules are.
```

So `Edit(.remember/**)` and `Edit(./.remember/**)` are the whole of the
`.remember/` protection, and the absence of a `Write` twin is correct rather
than a gap. This has been "fixed" twice by adding the twin back, and both times
it broke startup. **A reviewer who has not run the harness cannot see this;
check a permission claim against the harness before acting on it.**

**The `Edit` denies bind the agent's own tooling**, in both spellings each:
`.claude/scripts/**`, `.claude/sandbox/**`, `.claude/commands/**`,
`.claude/agents/**`, `.claude/settings.json` and `.claude/settings.local.json`.
Read the list, do not count it here — it has already grown twice, once inside
the pull request that introduced it. The review loops grant those
helpers by name, so a session that could rewrite one before invoking it would
make every fixed endpoint a fiction. The sandbox `Dockerfile` is on the list
for the same reason at one remove: it is a *build input to the security
boundary*, so a session able to edit it could add an entrypoint reading the
credentials the following `docker run` mounts in.

**The last three arrived with #33, and the argument for them is the first two's
applied one level up.** `commands/`, `agents/` and `settings.json` are the
files that *grant* what the first two protect. Ten commands carry an
unrestricted `Edit` or `Write` and three of them read untrusted input by
design, so a single applied edit could append a grant to a command's
`allowed-tools`, remove a line from this deny list, or rewrite
`.claude/agents/security-auditor.md` — whose read-only guarantee is precisely
its `Read, Grep, Glob` tool list, as `/security-sweep` says outright: read-only
there "is a property of the agent's tool grant, not a word in its prompt".

**`settings.json`'s own entry self-locks, and that is a working constraint, not
a curiosity.** Once it denies itself, the session cannot edit it again —
including to undo the edit. So a change to it is one edit that lands complete,
and it goes **last** in any PR that also touches `commands/` or `scripts/`;
split across two edits, the second is refused and whatever the first omitted
ships missing.

**The lock is not instantaneous, and the gap is a trap rather than a
convenience.** Measured on Claude Code 2.1.247, in the session that restored
these denies for the harness-bounds PR: immediately after writing
`Edit(.claude/scripts/**)` back, a `Write` creating a file under
`.claude/scripts/` **still succeeded**; minutes later an `Edit` under
`.claude/hooks/` and an `Edit` of `settings.json` were both refused. The file
is re-read, but not at the instant it is written.

So **a probe taken right after the restore reports the deny as inert and is
simply early** — and an agent that treats that reading as the answer will
conclude it still has access it is about to lose. Verify a restore by reading
the file, never by trying the thing it forbids.

**The practical consequence is an ordering rule for the human doing the
lifting**: restore the lines only when the work is genuinely finished, because
the window closes on its own schedule and a defect found afterwards needs the
lift granted again. That happened on the PR that added the argv guard — the
guard shipped refusing its own commit message, and the fix needed a second
lift.

**`.claude/settings.local.json` was the gap a review found**, and it is the
enumeration lesson in miniature: an exact-file rule cannot cover a sibling, and
Claude Code loads both files. Denying `.claude/**` wholesale was considered and
refused — `.claude/worktrees/` is where `/branch` puts working checkouts, so
that blanket would deny editing the repository itself while a worktree run is
live. A test pins both halves: every loaded settings file denied, and the
worktree root never denied.

Changing any of them is a human's edit, made with the deny lifted. Like the
push denies it is defence in depth — `Bash` redirection can still write a file
— but it removes the quiet path.

**`.claude/hooks/**` joined the list when the first hook landed, and the way it
joined is the lesson.** It had been excluded on a stated condition — "no hook is
configured here" — which was true, and which is the kind of exemption that
expires without anyone noticing, because nothing re-reads the condition when
the fact changes. #30's argv guard made it false. A hook is the sharpest case
on the list: it grants nothing, but it **runs on every Bash call**, so a session
able to rewrite one could delete its own guard and then act. `settings.json`
denies it, and a case in `test_grok_helpers.py` asserts the deny, so the next
hook arrives behind a control rather than behind a sentence that used to be
true.

**The external review runs in a container over a disposable clone — not a
worktree — and it has TWO residuals, which are not independent.** The boundary
is `.claude/sandbox/Dockerfile`; a worktree could not be the thing mounted,
because a worktree's `.git` is a file pointing back into this checkout, which
is the one path the container must not reach. No `gh` token, no SSH keys, no
host filesystem beyond the clone, non-root inside, and `bypassPermissions` is
no longer the risk it was because the blast radius is the box.

**Egress is not restricted** — the container reaches the network, and confining
it to `api.x.ai` needs an allow-list proxy Docker cannot supply alone. **And
the credential half is narrowed rather than closed**, which this paragraph
asserted as settled until #58: where `XAI_API_KEY` is unset or unusable,
`grok-review.sh` copies `~/.grok/auth.json` in, and that file carries a
refresh-token-bearing OAuth session for the x.ai account. The three things
enumerated as absent genuinely are; a fourth was never enumerated. **The open
residual is what makes the crossing one exploitable** — anything inside can
read the session and post it anywhere — so listing them as two independent
bullets is precisely what let the second read as finished. Prefer
`XAI_API_KEY`, which is scoped, revocable and crosses no file; on this host it
authenticates against a team with no credits, so the fallback is the path that
actually runs.

Stated here as well as in the script because `/ship` and both
sweeps cite `CLAUDE.md` as where the boundary and its residuals are recorded,
and it forwards here — the residual this file's header argues, not a second
claim about it. The
reviewer also has **no .NET SDK**, so `dotnet test` is this host's gate and
never the review's.

**A grant is not a whitelist, and this is the trap under every row below.**
`allowed-tools` is an **auto-approval list**: the harness documents that it
"does not restrict which tools are available: every tool remains callable, and
your permission settings still govern tools that are not listed". So *omitting*
a tool from a command's frontmatter withholds nothing — it only decides
whether the call prompts. Refusing a capability takes a **deny**:
`permissions.deny` in `.claude/settings.json` for the repository, or the
`disallowed-tools` frontmatter key for one command, which removes the named
tools from the pool while it runs. Precedence is **deny → ask → allow**,
first
match wins, so a deny beats every allow including a frontmatter one.
Measured, not read: a `general-purpose` subagent spawned fine under
`--allowedTools "Agent(Explore)"`, and was refused under
`--disallowedTools "Agent(general-purpose)"` with `Agent type 'general-purpose'
has been denied by permission rule 'Agent(general-purpose)' from cliArg` — and
again, from a command's own frontmatter, `from command`.

**A helper is the answer whenever a git grant is wider than the operation it
buys**, because **an allow rule is a prefix and cannot exclude a flag**. Each
of these was confirmed by running the offending form rather than reasoning
about it:

| Raw grant | What it also bought |
|---|---|
| `Bash(git switch:*)` | `--discard-changes` and `-C` — and the flags **combine**, so `git switch -fC <name> <start>` defeats any `Bash(git switch -C:*)` deny |
| `Bash(git worktree add:*)` | `-B`, which resets an existing branch rather than creating one |
| `Bash(git checkout -b:*)` | the trailing flag — `git checkout -b <name> -f origin/main` discards tracked modifications |
| `Bash(git branch:*)` | `git branch -fd <name>` — force and delete behind a spelling the `-d`/`-D`/`--delete` denies do not match |
| `Bash(git reset HEAD:*)` | `git reset HEAD --hard` — the `--hard` deny matches the other word order |
| `Bash(git log:*)`, `Bash(git diff:*)`, `Bash(git show:*)` | `--output=<path>`, which is an arbitrary file write with `--format=` choosing the bytes. Reproduced: `git log -1 --format=%s --output=<scratch>` wrote the commit subject, silently |
| `Bash(git fetch:*)`, `Bash(git pull --ff-only:*)` | a URL in the repository position, and `ext::<cmd>` is a git transport that **runs its argument as a command** |

**The reset grant does not narrow, and the attempt is the sharpest lesson
here.** It was "fixed" to `Bash(git reset HEAD --:*)` on the reasoning that
`--` turns a later flag into a pathspec. True of *git*, irrelevant to the
*rule*: an allow rule is a prefix match, and `git reset HEAD --hard` starts
with `git reset HEAD --`, so the narrowed grant admitted the exact command it
excluded — while the commit message said the hole was closed. **The git
behaviour was verified and the matching was not.** Anything whose safety
depends on what follows a token needs a helper, or a **deny**, not a cleverer
allow.

**A deny is the thing an allow cannot be: `*` matches at any position in it,
including the middle.** `Bash(git *--output*)` refuses `git log`, `git diff`
and `git show` carrying `--output` anywhere in the argument list, and leaves
plain `git log` alone — both halves measured, because a deny that blocked the
command outright would read the same from the failing side. Removing the three
read grants instead would have bought nothing: the harness treats read-only
forms of `git` as promptless built-ins whatever the allow list says, and its
own note is that "to require a prompt for one of these commands, add an `ask`
or `deny` rule".

**"Including the middle" is the measurement; the documented rule is wider, and
the two are worth keeping apart.** What was measured here is a wildcard between
two literals. The harness's own reference says a Bash rule's wildcards "can
appear at any position in the command, including at the beginning, middle, or
end" — so a pattern *starting* with `*`, which this repository had no precedent
for, is supported rather than merely untested. That was read out of the docs
after a branch shipped two such denies and flagged them as an unverified guess,
which is the right order: **write down which half you measured and which half
you looked up.** Two neighbouring constraints came from the same page and
match what is recorded above — the `:*` form is recognised only at the end of a
pattern, and a trailing `*` preceded by a space enforces a word boundary, so
`Bash(ls *)` misses `lsof` where `Bash(ls*)` catches it.

**It raises the cost of the naive spelling and it is not a boundary**, and the
distinction is the whole of the reset-grant lesson one paragraph up. A
permission rule matches the command *string*; the shell reassembles adjacent
quoted fragments before `exec`, so `--out''put=<path>` reaches `git` as
`--output=<path>` while never presenting contiguous `--output` to the matcher.
Copilot raised this against the rule as shipped and it is accepted. **The
concatenation is now MEASURED**, where this paragraph used to say it was not:
`printf '%s' --out''put=/tmp/x` prints `--output=/tmp/x`, and so does
`--"out"'put'=`. It had rested on documented quote-removal semantics because
the earlier probe was refused by the classifier layer — a second net worth
noting and not evidence. It is evidence now, and it says the deny was genuinely
defeatable rather than theoretically so.

**What closed it is the second of the two things named here as owed: a rule
over the executed argv rather than the typed string.**
`.claude/hooks/guard-git-argv.py`
is a `PreToolUse` hook on `Bash` that `shlex.split`s the command — the same
quote removal the shell performs — and judges the resolved argv, so the
fragments are rejoined before anything is compared and the dodge stops working.
**It reaches further than any rule could**, and that is the part worth carrying:
hooks run on every tool call in the loop, including the read-only `git` forms
the harness waves through as promptless built-ins, where no allow or deny rule
is consulted at all. Measured, not read — the docs do not say so, and a
`git log --out''put=` probe was refused by the hook with no file written.

The deny stays beside it as defence in depth. **A substring deny over a shell
command string can never be more than a speed bump**, which is still the
generalisation worth carrying past this one flag — what changed is that the
speed bump is no longer the only thing there.

**The hook's own residual, stated rather than left to be found:** `shlex`
resolves quoting and not expansion, so a flag assembled at run time —
`F=--output=x; git log $F` — arrives as the token `$F` and is not seen. Closing
that needs the argv after expansion, which no hook is given.

**This paragraph once claimed more than that, and a reviewer was right to say
so.** "Every spelling a caller can type literally" was false while
`git log "$(git push origin +HEAD:main)"` was one `shlex` token and two commands
to the shell — a command substitution is *executed*, not quoted away, and
calling it inert because it survived tokenisation intact is the same mistake as
reading a heredoc body as an argument list.

**That sentence then said both were closed, and they were closed in two
functions that disagreed with each other.** The stripper knew a heredoc body
was data; the extractor ran on the raw string ahead of it, with a quote tracker
of its own and no notion of heredocs or comments. So a substitution inside a
`<<'EOF'` body was refused although the shell never expands one there, and a
substitution inside a bare `<<EOF` body was **admitted** although it does — an
apostrophe in the body being an opening quote to the extractor and an ordinary
character to bash. Raised in review; both directions verified against the guard
as shipped, and the bash behaviour measured rather than argued.

**A second one was found while fixing it, one layer down and the same shape.**
`shlex.shlex` sets `commenters = "#"` and honours it at any character position,
where bash starts a comment only where `#` begins a word — so
`git log --grep=#x ; git push origin +HEAD:main` tokenised to three tokens, the
push left with the comment, and the hook reported no offence at all. Measured
under bash with a `git` shim: both invocations run.

Both are closed by giving the guard **one** model of where the shell expands
things — `expandable_regions`, over the quote-and-comment-aware scanner the
heredoc opener already used — rather than a second model per function. Command
substitution is now judged wherever bash would perform it and nowhere else.

**And then the bound was wrong for a fourth reason, which had nothing to do
with the shell at all.** Every version of this paragraph has named the residual
in shell terms — quoting, then expansion — while `git -c` sat one layer up:
`git -c "alias.x=!echo PWNED" x` runs the echo, and git executes a long list of
config keys besides — `core.pager`, `core.editor`, `core.sshCommand`,
`core.hooksPath`, `diff.external`, `credential.helper`. Measured in a scratch
repository. The hook admitted every one of them, because they are ordinary
arguments to an ordinary `git`, and nothing about the SHELL is involved. The
option is refused outright now, in the global position only, since `-c` after
`commit` means "reuse this message" and breaking that is how a guard gets
turned off. Enumerating the executing keys would be the deny-list this file
already refuses twice over.

**So state the bound as what has been looked for, not as what is left.** The
residual named today is what the shell **computes** rather than what a caller
writes, and it has two measured shapes: a flag or command assembled from a
variable (`F=--output=x; git log $F`), and a substitution whose OUTPUT becomes
the command line (`sh -c "$(echo 'git push origin +HEAD:main')"`). Closing
either needs the argv after expansion, which no hook is given. Both are pinned
as **admitted** in the suite, on the same argument as the degraded-check case:
a residual nobody can run is one the next reader assumes was closed.

Three earlier versions of that sentence were each falsified by a spelling
nobody had gone looking for, and the pattern is that the bound gets narrower
every time somebody probes rather than reasons. **It is a claim about the last
search, not a proof.**

**The `::` in a value collides with the `:*` suffix syntax, and the collision
fails silent in one direction and loud in the other.** `Bash(git *ext::*)`
passes settings validation and then matches nothing, because the trailing `:*`
is consumed as the prefix-wildcard form and the literal becomes `git *ext:` —
a probe of `git log -1 ext::foo` ran clean under it. Writing `Bash(git
*ext::**)` to dodge that is rejected at startup: *"The `:*` pattern must be at
the end."* So **`ext::` cannot be expressed in a Bash rule at all**, and the
transport is closed on the allow side instead — **and the two grants do it by
two different mechanisms, which an earlier revision of this paragraph ran
together.**

**Since #30 it is also closed on a third mechanism, and that one is not a
rule.**
`.claude/hooks/guard-git-argv.py` refuses an argv element containing `ext::` on
a git subcommand that takes a **repository** — `fetch`, `clone`, `pull`,
`push`, `remote` and their neighbours. **Not every element after `git`**, which
is what this sentence said until the scoping landed and did not reconcile it:
the transport is only meaningful where git expects a repository, and judging it
everywhere made a branch name or a path carrying the sequence indistinguishable
from a use of it — a commit body arguing about the transport included. A hook
is not bound by the pattern grammar at all,
which is why it can express what `Bash(...)` provably cannot — and the whole
argument below about which grant pins what remains worth reading, because a
hook is one file and defence in depth is the reason the allow side was narrowed
in the first place.

`Bash(git fetch origin:*)` **pins the remote**: a literal remote name occupies
the repository position, so a URL never reaches it. That is the control the
sentence describes, and it is real.

`Bash(git pull --ff-only)` pins **nothing** — no remote name appears in it at
all. What it does is drop the `:*`, so the grant is the documented
no-argument invocation and nothing else. Whether that closes
`git pull --ff-only ext::<cmd>` turns on the prefix-match rule stated in the
reset-grant paragraph above — **an allow rule is a prefix match** — which has
never been measured for the no-wildcard case. (Named rather than counted: a
numbered offset inside a section still being edited is how this repository's
callout totals used to rot.) If that holds for a grant without `:*` as well, the
pull side is still open and belongs in the residual inventory rather than in
this paragraph. Nobody has run the probe. Until someone does, treat the pull
grant as *narrowed, not proven* — the two words this repository keeps having to
tell apart.

**The probe that would settle it is not available from inside a session, and
that is a property of the question rather than a lack of trying.** Every exact
grant in `.claude/settings.json` — `Bash(git branch)`, `Bash(git remote -v)`
and the rest — is a **read-only** git form, and the harness treats those as
promptless built-ins whatever the allow list says. So a trailing argument on
one of them runs cleanly and demonstrates nothing about prefix matching: it
would have run either way. `git remote -v --verbose` was tried and is exactly
that null result. A decisive probe needs an exact grant on a command the
harness does not already wave through, which means adding a rule to a file the
session reads at startup — so it cannot be arranged from within the session it
would govern.

**A command's frontmatter is a grant like any other, and it is the one nobody
reads twice.** The first five rows above were all found in command frontmatter,
and for a while that supported a second claim — that the global file had it
right all along. **It did not, and the sixth row is where that broke**: the
`--output` write primitive sat in `.claude/settings.json`'s own allow list,
reachable from every command in the repository, and it had been read past for
as long as the frontmatter rows had. The lesson survives with its converse
attached: frontmatter is the grant nobody reads twice, and the global file is
the one everybody assumes somebody else already read.

**This paragraph is the inventory of grants wider than the operations they buy,
and no command file keeps a second total** — `ship.md`'s callout carried one,
and it went stale the moment a branch pinned that file's fetch grant.

**The entries are numbered and the numbering is stable; the headline count that
used to open this paragraph is gone on purpose.** It read "six" and was made
false by #56 closing the fourth, which is the third time a restated total in
this repository's guidance has rotted — `CLAUDE.md`'s callout totals and
`docs/testing.md`'s test count went the same way,
and the fix that held there was to drop the number and keep the named entries.
Ordinals are kept even where an entry is closed, so a cross-reference to "the
fourth" still lands, and each entry says its own state. Read the entries; do
not count them here.

Two are `/ship`'s:
`Bash(git worktree remove:*)` admits the `-f` that discards work, and
`Bash(gh pr merge --merge:*)` admits a trailing `--admin` that merges past
failing checks. Helpers are owed for both; until someone with the
`Edit(.claude/scripts/**)` deny lifted writes them, `/ship` carries them by
reporting its literal invocations, flags and all.

**Two more sat in the sweep command files rather than in this paragraph, and
both are now closed — which is worth recording because they were closed the
same way.** `Bash(mktemp:*)` took an arbitrary template, so it was a filesystem
write primitive able to create an empty directory or file anywhere the session
could write; `git-worktree-detach.sh` makes the directory itself now and prints
it, and both sweeps dropped the grant. `Bash(gh label create:*)` was
create-*or-overwrite* — `--force` updates an existing label's colour and
description, and `-R` put that write in any repository — and it was held as two
prose rules; `gh-label-ensure.sh` leaves no free parameter, taking one name out
of a fixed six-entry case and resolving the repository from the checkout. **The
root cause both shared is the one that generalises**: each was documented by the
operation it was added for rather than by what its prefix admits, and reading
the tool's own `--help` found something every time it was done. That they were
inventoried in a command file and not here is the same drift this paragraph
warns about, running the other way.

The third is `Bash(git fetch origin:*)`, which no longer admits a URL but still
admits a trailing flag; `--upload-pack`, `--receive-pack` and `--exec` are
denied by name, so what is left is the flag nobody has enumerated yet. The
honest fix is the helper the transport issue asked for — a
`git-fetch-origin.sh` taking a branch name and nothing else.

The fourth **was** `/review-copilot`'s three unfiltered comment feeds, and #56
closed it. It is kept here in the past tense rather than deleted, because what
made it survive three revisions is worth more than the fix: each revision
narrowed the claim and none of them narrowed it to something enforceable.

**What landed.** All three feeds are behind helpers — `pr-review-bodies.sh`,
`pr-review-comments.sh`, `pr-issue-comments.sh` — filtering on one allow-list
declared once in `copilot-authors.sh` and each printing an admitted/dropped
count. `Bash(gh pr view:*)` is gone from `review-copilot.md`'s frontmatter,
which is the half that makes it enforcement rather than courtesy: that command
used `gh pr view` for nothing but the two GraphQL feeds, and `settings.json`
carries no `gh` allow, so a raw call prompts — a stall in the unattended loop
instead of a silent pass.

**One file was not enough, and the review of the PR that closed this is what
established it.** `/ship` invokes `/review-copilot` as a skill while holding
its own frontmatter grants, and `allowed-tools` entries are cumulative
auto-approvals rather than a whitelist — so a grant removed in one file and
kept in its caller withholds nothing on the unattended path, which is the path
the issue was about.

**And `gh pr view` was not the only door, which took a third review round to
find.** `gh pr list --json reviews,comments` returns the same review bodies and
issue comments for every pull request at once — measured here, a 2,457-character
Copilot review body out of `gh pr list --state all --limit 1 --json
number,reviews`. Three commands kept that grant for the harmless job of finding
a branch's pull request, and it was a complete bypass of all three filtering
helpers. **No command grants `Bash(gh pr view:*)` or `Bash(gh pr list:*)` any
more**: `ship.md` reads state through `pr-state.sh`, `pr.md` feeds the closure
gate through `pr-closure-input.sh`, all three resolve a branch's PR through
`pr-for-branch.sh`, and every one fixes its field set, because a caller that
chooses fields can choose `reviews`.

**The gate that pins this is an ALLOW-list of `gh` subcommands, and it is the
second one in this repository to be rewritten that way.** Its first version
banned `gh pr view` by name and passed while three files still granted
`gh pr list` — a deny-list passing every spelling nobody thought of, which is
exactly what the Grok verdict check did before it was inverted.

**This paragraph used to name `gh issue view` and `gh issue list` as still
admitted, and #150 removed both.** The measurement it rested on is unchanged
and was never the thing in question — `gh issue view <pr-number> --json
comments` does return an empty array, because `gh` keeps issues and pull
requests distinct even though GitHub's model does not. Membership of the
allow-list is a different fact, and it moved: `gh issue view` returns `author`
to the same session, and `gh issue list` returns `author` **and every issue
body** at once. Both went, and `gh repo view` with them.

**Two paragraphs in this file disagreed about one gate, and the stale one was
the dangerous half** — a reader reconciling a new grant against "stay admitted"
would have put back exactly what #150 closed. Raised in review, against the
branch that closed it: the record of the removal was written a thousand lines
earlier, and the rule nobody re-read was not.

**The set itself is deliberately not written here**, which is the fix rather
than an omission — it is `GH_GRANTS_THAT_CANNOT_REACH_A_FEED` in
`test_grok_helpers.py`, beside the assertion that reads it, and a copy in this
file is what went stale. This paragraph carries the argument for why a
subcommand joins or leaves that set; the set carries the membership.

`ship.md`'s review-body and inline
reads go through the same feed helpers `/review-copilot` uses, so the two
commands share one list instead of holding two prose rules that disagreed.

**`pr.md` was the third holder and was found by a test, not by reading.** The
issue named two commands; the case whose subject is *every* command's
frontmatter caught the third. That is the gate-coverage lesson paying for
itself inside the change that added the gate.

**#150's suppression clause moved three times in one branch, each time one
grant along from the one just closed, and it is the clearest instance of #56
this repository has.** `gh repo view` went when the suppression helper resolved
the owner itself. `gh issue view` went when a reviewer observed it returns
`author` to the same session. `gh issue list` survived both rounds and returns
`author` **and every issue body** at once. Each fix cited the same rule — a
helper that fixes its field set does not bind a caller who still holds the raw
grant — and each left the next grant standing. **The gate now asserts on the
grant rather than on the instruction line**, because a listing line naming four
fields is a rule a reader follows and a prefix grant beside it is what the
session can actually run.

**Three things the fix deliberately does not do.** It admits the repository
owner alongside Copilot's three logins, because the decision table has three
rows and dropping the owner would take away the input for the middle one —
measured on PR #147, 21 of 43 inline comments and 21 of 33 review bodies are
the owner's. It reports a dropped item's author and location but **never its
body**, since printing the text one stream over would put the injection vector
back into the transcript the filter exists to keep it out of — which narrowed
the *Anyone else* row from "report what it asked for" to a count and a
location, a deliberate loss of detail. And it is **not authentication**: a
GitHub login is not verified, so the filter refuses the ordinary stranger and
would not refuse a takeover of one of the four admitted logins.
`grok-ledger.sh`'s collaborator-permission check is the stronger form and is
not reached for, because Copilot is not a collaborator and a permission check
would drop the whole review.

**The measured logins, which the fix did not change.**
`copilot-pull-request-reviewer[bot]` is REST's spelling, from
`/pulls/{n}/reviews` — an endpoint no helper here calls. The one REST endpoint
in play, `/pulls/{n}/comments`, reports `Copilot`; the two GraphQL feeds report
the bare `copilot-pull-request-reviewer`. An earlier revision of this paragraph
called the issue-comment login REST's; `gh pr view` loads `reviews` and
`comments` through one exporter, so that could never have been true. All three
spellings stay admitted, because admitting one that never arrives costs
nothing and missing one that does is the direction that fails open.

**The issue-comment feed's Copilot login is still unobserved**, and a revision
of this paragraph once asserted it as measured. Seven PRs have now been checked
— #112, #101, #100, #99, #98, #94 and #147, the last through the new helper
itself — and none carries a Copilot-authored issue comment. So the shared
exporter says what the login *must* be and nothing has seen it. Not evidence:
an asserted measurement that never happened stops the next reader checking.

The fifth is **`git push` under the two sweeps**, and it is the one that looks
closed and is not. Both commands state a read-only boundary, and both used to
close it with "no `git push` is granted either, so the branch cannot move" —
which reads an *absence* as a control, the exact rule the sentence beside it
had just retired. `.claude/settings.json` **allows** `Bash(git push origin:*)`
and `Bash(git push -u origin:*)` globally, so a push of the current branch does
not prompt at all; only force-pushes and pushes to `main` are denied. Naming
`git push` in each sweep's `disallowed-tools` is the obvious fix and is
**unverified**: that key's `Bash(...)` form has never been measured here — the
`Agent(...)` form is what was — and a nested `claude -p` probe could not
separate a rejected pattern from a command that failed to load. Both files now
state the residual instead of claiming the control.

The sixth **was** the `--output` deny itself — the inventory's one entry that
is a *deny* rather than an allow, listed because a deny over a command string
is defeated by shell quoting. **#30 closed it, and not by improving the rule.**
`.claude/hooks/guard-git-argv.py` judges the resolved argv, so the quoted
spelling is refused too, and it fires on the promptless read-only `git` forms
no rule is consulted for. The deny stays as defence in depth and the three read
grants stay with it, because removing them still buys nothing against a
built-in. What survives as residual is one line rather than a grant: a flag the
shell *computes* — `F=--output=x; git log $F` — is not visible to a hook that
resolves quoting but not expansion.

**A seventh thing is a gap in the mechanism rather than in a grant.** Pinning a
command to one subagent type is a **deny list of every other type**, because
the harness has no "only this type" allow — so `security-sweep.md` and
`bug-sweep.md` each enumerate the registered types that hold a shell, an editor
or the network, and **a newly added agent under `.claude/agents/` is admitted
by default** until someone adds it to both lists. That is the shape this
repository already knows rots; it is taken here because the alternative on
offer is prose.

**The eighth was the push deny-list (#23), and it closed the way the sixth
did.** Two broad allows — `Bash(git push origin:*)` and the `-u` form — paired
with a list of exact spellings, so `git push origin +HEAD:main` was a force push
to `main` carrying neither `--force` nor the literal `origin main`, and five
more slipped alongside it: `origin :branch` deletes where the `--delete` deny
does not look, `origin HEAD:refs/heads/main` is the fully-qualified spelling,
`origin feature --force` puts the flag where a leading-flag deny cannot reach.
**Adding four more denies was refused.** Deny-list enumeration trails git's
refspec grammar forever, and the grammar keeps growing — which is the issue's
own conclusion — and judging three *properties* instead was not enough either.
Two review rounds took that apart: `--force-with-lease=<ref>` is not equal to a
set entry, git accepts `--for` as an abbreviation, `-fv` bundles, `--branches`
and `--all` and `--mirror` carry no refspec to inspect, `refs/heads/*` includes
`main` while equalling nothing, and bare `git push origin` names no destination
at all. That is the deny-list trailing the grammar in parser form. **The
question is inverted now**: a push is refused unless every part of it is
recognised, so the spelling nobody has listed is refused for being
unrecognised rather than admitted for being unlisted. Its suite carries the
six the issue listed and six it did not, plus the three pushes `/ship`
actually makes, because over-reach here breaks the delivery chain and would
be found at the worst moment.

**The ninth is `/review-branch`'s pair of `dotnet` grants, and what makes it
worth its own entry is that the enumeration guarding it could never have
worked.** That command holds `Write` and held `Bash(dotnet build:*)` and
`Bash(dotnet test:*)`; its `disallowed-tools` denies every tracked root file,
read from `git ls-files` by a test so a new one is a red build. But MSBuild
imports `Directory.Build.targets` into every build of every project beneath it,
and that file **does not exist in this repository** — so it is on no list read
from what exists, and no care about that test could have put it there. Write it,
run the build the command already had, and an `Exec` in it runs. Measured, in a
scratch tree: the target fires and `dotnet build` reports success. Raised in
review.

`dotnet build` was a grant the command never used — its body names
`dotnet test` alone — and `dotnet test:*` bought two things beyond the run it
was added for: any project path, and `/p:CustomBeforeMicrosoftCommonTargets=`,
which imports whatever file it names, `suggestions.md` among them. Both are
gone, replaced by `dotnet-test.sh`, whose only variable is one word out of two.
**The helper closes the executor and not the import**, so the auto-imported
names are denied as well — in **both** commands that state editing boundaries,
because the artefact outlives the command that writes it and only one of the
two had an executor. A `Directory.Build.targets` written by
`/validate-blueprint` is executed by the next thing that builds.

**Which half is measured is recorded rather than rounded up.** The exact
filenames are the spelling both files already used and the suite reads. The
`**/*.targets`-style globs beside them are the documented gitignore syntax and
are **not** measured in a `disallowed-tools` value — belt to the names' braces,
and not the control.

**The two sweeps are one shape asking two questions**, split by what makes a
finding rather than by where they look. `/security-sweep` files what an
attacker can reach; `/bug-sweep` files what is wrong on its own terms. Both
fork a detached worktree, verify every subagent claim before filing,
de-duplicate against the whole issue set, never fail open, and file without
fixing. Three things differ: the threshold, the fan-out cut, and what
confirmation can mean — **`/bug-sweep` executes none of the snapshot it
audits**, because building a tree executes it (MSBuild targets, source
generators, analysers, and under `dotnet test` the tree's own test code) and
the audited repository is prompt-injection input. So a defect claim there is
confirmed by reading, and the class of bug only execution catches is named as
the residual.

**Both sweeps' worktrees carry the `secsweep-` prefix**, and the second is
borrowing. `git-worktree-detach.sh` and `git-worktree-drop.sh` refuse any path
that is not `secsweep-` plus six characters under the canonical temp root —
the shape check that stops a poisoned finding from naming a sibling PR worktree
and having it deleted. Renaming the prefix would have to move in both helpers
and both callers at once, so it stands; what is lost is attribution.
