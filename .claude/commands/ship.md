---
description: Start from a clean main, fork a worktree where one can be forked, branch, commit, push and open a PR, loop the external reviews — Grok until two consecutive clean passes, Copilot until one — then merge the PR and tear the workspace down. Decides for itself rather than stopping to ask
argument-hint: "[what the change does] — omit and each step derives its own"
allowed-tools: Read, Grep, Glob, Write, Skill, EnterWorktree, ExitWorktree, Bash(git status:*), Bash(git diff:*), Bash(git branch --list:*), Bash(git branch --show-current), Bash(git branch -a), Bash(git log:*), Bash(git fetch:*), Bash(bash .claude/scripts/git-branch-create.sh:*), Bash(bash .claude/scripts/git-worktree-fork.sh:*), Bash(bash .claude/scripts/git-switch-existing.sh:*), Bash(git rev-parse:*), Bash(git worktree list:*), Bash(ls:*), Bash(git add:*), Bash(git commit:*), Bash(bash .claude/scripts/git-unstage.sh:*), Bash(git push -u origin:*), Bash(git push origin:*), Bash(wc:*), Bash(gh pr create:*), Bash(gh pr view:*), Bash(gh pr list:*), Bash(gh pr checks:*), Bash(gh pr merge --merge:*), Bash(git pull --ff-only:*), Bash(git worktree remove:*), Bash(git worktree prune:*), Bash(bash .claude/scripts/grok-ledger.sh:*), Bash(bash .claude/scripts/copilot-request.sh:*), Bash(bash .claude/scripts/copilot-request-count.sh:*), Bash(bash .claude/scripts/pr-review-comments.sh:*), Bash(bash .claude/scripts/pr-review-threads.sh:*), Bash(bash .claude/scripts/grok-review.sh), Bash(sleep:*)
---

Take the working tree from wherever it is to a merged PR. Description:
$ARGUMENTS — if empty, each step derives its own.

## This command owns no rules

`/branch`, `/commit` and `/pr` hold the branch-naming table, the
commit-splitting test and the PR body form. **Load each and follow it. Do not
restate them here** — a fourth copy of the naming table is exactly the drift
this repo exists to close, and a chainer that paraphrases the steps it calls is
the worst place for that copy to live.

What this command adds is the handoffs: which steps are still owed, and where
the sequence is allowed to stop.

## It runs to the end, and the end is a merged PR

`/pr` pushes the branch itself, so the chain reaches an open PR without waiting
for anyone, and step 7 merges it. Steps 5 and 6 sit between: Grok reads the
branch and `/review-grok` triages what it found, then Copilot reads the PR and
`/review-copilot` triages that. When both loops have finished — however they
finished — the PR is merged, the session returns to the main checkout and the
worktree is removed.

**Nothing in this chain stops to ask.** Where an earlier version handed a
finding back — step 2's checks, a `Needs a decision` row from the Grok triage,
an open `Ask` thread from the Copilot one — the run now takes the recommended
option itself and keeps going. That is the caller's standing instruction and
not a judgement about the findings.

**Deciding is not the same as going quiet, and this is the part that makes the
change survivable.** A decision taken here is written down where the person who
would have been asked can find it: an `Ask` thread gets the answer posted on
the thread and is then resolved, a `Needs a decision` row is answered in the
resolution record, and both appear in the report with the option taken and the
one rejected. A silent decision is the failure mode this rule creates; a stated
one is the thing it trades an interruption for.

**Resolving `Ask` threads is load-bearing rather than tidy.** Step 6's
all-resolved state is defined over *no unresolved threads*, so a thread left
open by an earlier round can never be reached past — the loop would run to its
ceiling on every subsequent round with nothing new to fix. Answer, resolve,
carry on.

**Three things still stop the chain**, and none of them is a decision somebody
could have made differently:

| | |
|---|---|
| A helper exits non-zero | The step did not run; a report that says otherwise is false |
| CI is not green at step 7 | A merge onto a red `main` is not a judgement call |
| The PR is not mergeable | Conflicts are the caller's tree, not this chain's |

The last two are worth separating from the first. A helper failing is a
question about *this* run; those two are questions about the repository's
state, and no recommended option exists for either.

**A review loop hitting its ceiling is not on that list, and putting it there
was a real confusion rather than a wording slip.** A ceiling ends a *loop* —
the loop reports itself unconverged and step 7 merges anyway, because a budget
running out is not a verdict. Reading it as a chain stop would hold every PR
whose reviewer had more to say, which is the opposite of what step 7 decides.

**The checks carry the weight the stops used to, and they now carry more of
it.** Under the old blanket `Bash(git push:*)` deny this command could not
finish at all: it stopped before the push, and that stop was the last cheap
moment to change the work. Then the narrow denies let it reach an open PR, and
a human still saw the PR before it landed. Now it merges. Step 2 is the only
thing left between a bad edit and `main` that is not a review bot, so
**skipping it is no longer a minute saved — it is the last gate**.

## Resume, don't restart

**Read the state first and run only what is still owed.** Every step is
skippable because an earlier run already did it:

**Step 0 runs on every entry, resumed or not**, and step 7 closes every one
that reaches a merge — so the rows below say what is owed *between* them:

| State | What is owed |
|---|---|
| On `main` | All of it — step 1 forks the workspace when the tree is clean and the parent is writable, and otherwise branches in place |
| On a branch, tree dirty | Checks, `/commit`, push, `/pr` |
| On a branch, tree clean, unpushed or ahead | Push, `/pr` |
| On a branch, tree clean and pushed | `/pr`, then the review loops |
| On a branch with an open PR | The review loops (steps 5–6), Grok before Copilot — and, if the tree is dirty, checks, `/commit` **scoped to the implementation paths** and a push first, so the reviewers read what the PR will actually carry. Never unscoped while `suggestions.md` is on disk: that file is Grok's working state, and the unscoped form sweeps untracked files into the commit |
| On a branch whose PR is **already merged** | **Step 0 alone, and then the run is over.** `gh pr view --json state` reading `MERGED` is the check, and it comes before the review loops rather than after them — re-requesting a review on a merged PR spends a round of somebody's budget on a branch nobody can change. Step 0's teardown is a complete one (switch, pull, remove, prune), and step 7 has nothing left to do: there is no PR to merge |

**Step 0's teardown targets a worktree that is already finished; step 7's
targets the one this run just merged. Exactly one of them owns any given
directory.** A resumed run that starts inside its own unfinished worktree stays
there — step 0's table says so in its second row, and that row is what keeps
this step from stranding the branch it was meant to tidy up around.

**The row above is the case where the two could collide, which is why it ends
the run at step 0.** A session standing in a worktree whose PR is already
merged is finished by step 0's first row *and* would be "this run's" by step
7's. Both tearing it down means the second `git worktree remove` runs against a
path that is no longer a worktree, exits non-zero, and stops the chain on a
helper failure with no defect behind it. So that row is step 0 and nothing
after: there is no merge left to perform, and the teardown has already
happened.

**The workspace is part of that state**, and it is read the way `/branch`
step 0 reads it: `git rev-parse --git-dir --git-common-dir` differing, with no
`--show-superproject-working-tree` to make it a submodule, means this session
is already inside this PR's worktree. Then every row above is owed *there* and
nothing forks a second directory. A run that starts in the main checkout on
`main` is the only one that can fork a workspace at all — and only with a clean
tree and a writable parent, per step 1's two exceptions.

**The Grok loop's clean state cannot be read from the tree**, so a resumed run
re-enters step 5 rather than inferring it ran: `suggestions.md` is absent
before the first review and after a clean one, and the two states are
indistinguishable. Re-entering is safe because that loop is idempotent against
a clean branch — a Grok full review of nothing writes nothing — and that re-run
is the proof, where the inference was a guess.

**The Copilot loop is the opposite, and deliberately so**: its clean state is
not a missing file but a landed review, which is durable, on the PR, and
carries the commit it read. A last landed review by
`copilot-pull-request-reviewer` with no comments, nothing in its suppressed
block, no unresolved threads on the PR, a `commit` oid equal to the pushed
head, **and no `review_requested` event newer than it**, is **all-resolved** —
step 6 is not owed, and re-requesting would be asking a question already
answered on the record. Anything pushed after that review un-marks it, because
the oid no longer matches and the clean verdict is then about a state the PR no
longer carries. That pinning is what makes the inference safe here where it was
a guess for Grok: the artefact says which commit it read, and `suggestions.md`
never could.

**The newer-request clause is not redundant with the oid**, and leaving it out
is how a resume ships past a review it never read. A run interrupted between
requesting a round and its landing leaves the PR in a state where the
*previous* clean review still satisfies every other condition — same head, no
threads, nothing suppressed — so a resume would call it all-resolved while a
review it has not seen is in flight.

**Count the two sides; do not compare timestamps.** The check has to be one
this command can actually run, and a timestamp is not:
`copilot-request-count.sh` returns an integer and nothing else, and there is
deliberately no raw `gh api` grant here to fetch anything richer. It needs
none. The helper counts
`review_requested` events for Copilot, this loop makes exactly one per round,
and each lands exactly one review — so **a request is outstanding when that
count exceeds the number of landed Copilot reviews**, and both numbers are
readable with what is already granted (`gh pr view <n> --json reviews` supplies
the second). When one is outstanding, wait for its review rather than
inheriting the verdict of the one before it. A request that never produces a
review is the timeout case this step already covers, reported as the loop not
having finished rather than clean — so the comparison fails closed, which is
the direction it must fail in.

`git status -sb`, `git branch --show-current`, the `rev-parse` pair above,
`gh pr list --state open` and a look for `suggestions.md` (it decides recheck
versus full review inside step 5, not whether step 5 runs) answer all five rows
and the Grok half. Read them before doing anything.

**All-resolved needs three reads, not one**, because no single call carries the
three signals it is defined over. `gh pr view <n> --json reviews` gives the
review bodies, their suppressed blocks and the `commit` oid — and nothing else:
**it does not return inline review comments, and it does not return thread
resolution state.** Deciding step 6 is not owed from that call alone would skip
two of the three clean signals while reporting that all three were checked. So
the resume runs the same read-only intake `/review-copilot` does:

```bash
bash .claude/scripts/pr-review-comments.sh <n>     # inline comments
bash .claude/scripts/pr-review-threads.sh <n>      # <thread-id> <isResolved> …
```

Both are read-only with fixed endpoints, which is why they can be granted to a
step that only wants to look. An unresolved thread from an earlier round is
exactly the state a fresh clean review never repeats, and it is the one the
oid cannot see.

**The two reads are scoped differently, and getting that backwards fails in
both directions.** `pr-review-comments.sh` returns *every* inline comment the
PR has ever carried, replies included — so on any PR whose earlier rounds found
something, its output is non-empty forever and a resume reading it whole would
never see a clean review again. It has to be joined to the candidate review.
**Threads are the opposite and stay global**: an unresolved thread from round
three is still owed at round nine, which is the entire reason that signal
exists.

**Join on the timestamp, not on a review id — the two sides do not share
one.** `gh pr view --json reviews` reports a GraphQL node id
(`PRR_kwDOTuTjXM8AAAABI_IalQ`) and the REST helper reports a numeric
`pull_request_review_id` (`4898036373`). Comparing them matches **nothing**,
which does not merely fail — it drops every comment and reports a review full
of findings as clean. That is the one direction this check may never fail in,
and it is the same GraphQL-versus-REST split the reviewer's own login already
has two paragraphs above.

What both sides do carry is the time, and they agree to the second: a review's
`submittedAt` and its comments' `created_at` are the same instant, because the
comments are created by the submission. So the candidate's findings are the
comments authored by `Copilot`, with **no** `in_reply_to_id` — a reply is a
triage answer, not a finding — and `created_at` no earlier than the candidate's
`submittedAt`. Nothing later than the last review exists, so that set is
exactly its own.

**Each loop's check count lives on the PR itself, where any resumed run can
read it.** Step 5's checks are ledgered as PR comments — a reservation
posted before each `grok-review.sh` invocation, released only by an exit-12
skip (step 5 has both forms) — and a resumed run recovers the count as the
highest N reserved and not released; an unreleased reservation counts as
spent, and no ledger comment means a fresh PR with nothing spent. The ledger
carries convergence as well as spend, because spend alone cannot tell a loop
that converged on its last allowed check from one the ceiling cut off. The
`converged` marker settles only that question — the report at the ceiling —
and never excuses re-entry: the rule above stands, a resumed run re-enters
step 5, and the marker is not pinned to a commit, so commits landing
after it still get their re-review from the re-entry, budget allowing. That
last clause is exactly what step 6's oid gives it and this marker cannot, and
it is why only one of the two loops can be skipped on a resume. Any
later reservation supersedes the marker, and it is read with
`bash .claude/scripts/grok-ledger.sh <n> status` — the same author
verification as the count, because a raw-comment read would take the
marker from anyone. Step 6 needs no marker for the same
question — its outcomes are already on the PR, so a resumed run reads the
last landed review (comments and suppressed block alike), the commit it read
and the unresolved-thread list before declaring that loop owed, all-resolved
or exhausted. The
count read goes through the same helper —
`bash .claude/scripts/grok-ledger.sh <n> count` — because PR comments are
unauthenticated state: on a public PR anyone can post a line that imitates
the ledger, so only the helper's exact shapes count as state, and only from
authors whose repository permission the helper verifies as write or better —
PR-local, not account-local, so a resume under another authorised login
reads the same count. The last event per N wins.
A ledger read or write that fails stops the chain rather than
guessing — a cap that resets when its state goes missing is no cap, the
same argument as never calling a branch clean because asking failed.

## Steps

0. **Start from the main checkout, on an up-to-date `main`, with no leftover
   worktree.** A run that begins inside the *previous* PR's directory is the
   failure this exists to prevent: step 1 reads "already on a branch", adopts
   it, and the whole chain commits this change onto the last one's branch.

   **Being in a worktree is not by itself the problem — being in a *finished*
   one is.** `git rev-parse --git-dir --git-common-dir` differing, with no
   `--show-superproject-working-tree`, says the session is in a linked
   worktree; what decides whether to leave it is the branch it holds:

   | Where the session is | Do |
   |---|---|
   | In a worktree whose branch is **finished** | `ExitWorktree({action: "keep"})`, then the teardown below on the directory just left |
   | In a worktree whose branch is **unfinished** | **Stay.** This is a resumed run and that directory is its workspace |
   | In the main checkout on a **finished** branch | `bash .claude/scripts/git-switch-existing.sh main`, when `git status --short` is clean |
   | In the main checkout on an **unfinished** branch | **Stay.** `/branch` puts a branch here whenever `main` was dirty, so this is an ordinary resumed run |
   | In the main checkout on `main` | Nothing but the teardown below |

   **Finished means a merged PR, or no PR and nothing ahead of `origin/main`.**
   Everything else is unfinished, including a branch whose commits are all
   pushed and which has simply not reached `/pr` yet.

   ```bash
   gh pr view --json state          # MERGED, or no PR at all
   git log origin/main..HEAD        # empty means nothing of its own
   ```

   **The word to avoid here is "unpushed", and avoiding it is the whole of this
   fix.** The resume table below uses it in git's ordinary sense — commits not
   yet on `origin/<branch>` — and its rows need that reading. Spelling the
   Finished predicate as "nothing unpushed" imported the other meaning: a clean
   branch, fully pushed, with no PR yet is *nothing unpushed* and is exactly
   the state that owes `/pr`. Step 0 would have left it, and step 1 would have
   refused a name that already exists — the same stranding this chain has now
   closed in three shapes rather than two, and the third arrived through a word
   rather than through a missing row.

   **Two rows say Stay, and they are one rule in two shapes: never walk away
   from unmerged work.** Leaving an unmerged *worktree* strands the branch —
   the session returns to `main`, step 1 forks, and `git-worktree-fork.sh`
   refuses a name that already exists, leaving the commits in a directory
   nobody is in. Leaving an unmerged *in-place* branch does the same thing
   without the directory: `git switch main` succeeds, step 1 forks, and the
   same refusal lands on the same name.

   The second shape is easy to miss because the in-place branch is the
   *exception* in step 1 rather than the ordinary case — and it is exactly what
   `/branch` produces every time `main` was dirty, which is every time a change
   is already half-written when the chain starts. The two commands above answer
   which row applies, and they are needed **together**: the PR state alone
   cannot see a branch that never opened one, and the commit count alone cannot
   see one whose PR is merged.

   **`ExitWorktree` with `keep`, never `remove`.** The remove form only works
   on a worktree this session created with `EnterWorktree`, and a `/ship`
   worktree comes from `/branch`'s `git-worktree-fork.sh` — so `remove` refuses
   with *this session is not the owner*, which is another stop with nothing
   behind it. Leave, then tear down with git.

   Then, in the main checkout:

   ```bash
   git worktree prune                      # registrations whose directories are gone
   git worktree list                       # what is actually still there
   git pull --ff-only                      # ONLY when HEAD is main — see below
   ```

   **The pull is guarded on being on `main`**, because the one row above that
   leaves the session elsewhere is the dirty-checkout row, and a bare
   `git pull --ff-only` there updates whatever branch HEAD is on. Prune and
   list are safe anywhere; the pull is the only one that reads HEAD.

   **Remove a sibling worktree only when its branch is merged and its tree is
   clean**, and let git decide the second half:

   ```bash
   git worktree remove ../<checkout-name>-<slug>
   ```

   Without `-f` that command **refuses a worktree holding uncommitted or
   untracked files**, which is the guard rather than an inconvenience — the
   same refusal `/security-sweep`'s teardown uses. A worktree it declines to
   remove is left where it is and named in the report; do not reach for `-f`,
   which is the one spelling that discards somebody's work.

   > **Two grants in this file are wider than the operations they buy, and both
   > are known residuals rather than oversights.** A prefix rule cannot exclude
   > a *trailing* flag — the argument the push rules already make — so
   > `Bash(git worktree remove:*)` admits the `-f` this file forbids, and
   > `Bash(gh pr merge --merge:*)` admits a trailing `--admin`, which merges
   > past the failing checks step 7 treats as a hard stop. Pinning `--merge` at
   > the front does close the *method* — `gh` refuses two of `--merge`,
   > `--squash` and `--rebase` together — so that half is real; the bypass half
   > is not.
   >
   > Every comparable case in this repository is fixed by a helper that spells
   > its own flags, and the two that exist (`git-worktree-detach.sh`,
   > `git-worktree-drop.sh`) shape-check their argument against
   > `secsweep-??????` and therefore refuse a PR worktree by design. Two more
   > are owed here; until someone with the `Edit(.claude/scripts/**)` deny
   > lifted writes them, both rules are carried by this file, like the `[`
   > placement rule in `CLAUDE.md`.

   Deleting the merged **branch** is not part of this. `git branch -d` is
   denied in `.claude/settings.json`, deliberately, and a merged branch costs
   nothing but a line in `git branch`. Name it in the report and leave it.

1. **`/branch`**, passing $ARGUMENTS. Skip if already off `main`.

   **This step is also where the workspace comes from, and it has two
   outcomes.** From a clean `main` with a writable parent, `/branch` forks a
   sibling worktree and moves the session into it: **every step below then runs
   in the PR's own directory** and this checkout stays on `main`. On either
   exception — a dirty `main`, because uncommitted work cannot follow a fresh
   checkout without a stash or a patch and both are refused here, or a parent
   that is not writable, where there is nowhere beside the checkout to put
   one — it branches in place, and the rest of the run happens in the main
   checkout on the new branch.

   `/branch` owns the naming, the placement and both exceptions, so do not
   restate the rules; do report which outcome happened, because it is what
   decides where every path in this run is rooted.

   `/branch` stops when it is already on a branch and asks whether this is a
   second change or a continuation. In a chain that stop is wrong — being on a
   feature branch is the normal state of a resumed `/ship`. Take the current
   branch as this change's branch and carry on, but **say that you assumed it**
   and name the branch, so a tree that has drifted onto the wrong one is visible
   before anything is committed to it. The same goes for the directory: name
   the worktree the run is in, and if it is the main checkout say that too.

2. **Checks** — `/validate-blueprint`, plus `/check-links` when the change
   touched links, cross-references or nav footers.

   **Before `/commit`, not after.** A defect found after the commit costs a
   second commit or a rewrite; found here it is an edit. This is also the step a
   chained workflow silently drops, which is why it is a step rather than a
   footnote: `/pr` requires the body to state whether these ran, so skipping
   them quietly makes the PR body untrue.

   **Fix what they find, then run them again.** This step used to stop and hand
   the finding back; it no longer does, and it is the step that changed most in
   losing that. A blueprint contradiction has one correct resolution far more
   often than it has two — reconcile to whichever side the rest of the system
   depends on, exactly as `/validate-blueprint` already instructs, and record
   the direction in the commit body.

   Where a finding genuinely has two defensible answers, take the one the
   surrounding argument supports, say which you rejected, and put both in the
   report. That is what this chain now does everywhere; the difference here is
   only that nothing downstream will catch a wrong choice, because the reviewers
   read the branch and not the specification.

   **Do not skip these to reach the PR sooner.** Step 7 merges, so this is the
   last gate before `main` that is not a review bot. If they are skipped for a
   reason, the reason goes in the PR body and in the report — `/pr` requires the
   body to state whether they ran, and a body that says they did is false
   otherwise.

3. **`/commit`**. Skip if the tree is clean.

   Do not collapse the split to save a step. The commits are what `/pr` writes
   its body from, so a single lumped commit costs twice.

4. **Push, then `/pr`.** Both belong to `/pr` — it reads `git status -sb` and
   pushes only what is owed, then opens the PR, deriving its own title from the
   commits. $ARGUMENTS described the branch, not the PR.

   The push is called out here rather than left inside step 4's prose because
   it is the only action in the whole sequence that another person can see.
   A chain that reaches the remote silently is a chain nobody audits, so it
   gets a line in the report whether or not it did anything.

   `/pr` stops on one thing: an open PR already exists from this branch.
   Updating it is a decision, not a default — and inside this chain, step 5
   is that decision already made: pushes that close review findings update
   the PR without asking again.

5. **The review loop.** Once the PR is open, alternate the two halves of the
   external review until it has nothing left to say:

   1. **`/review-branch`, run by Grok, not by you** — the second opinion is
      the point, and a review run by the author's own model is not one:

      ```bash
      bash .claude/scripts/grok-review.sh
      ```

      The helper runs Grok **in a container** (`.claude/sandbox/Dockerfile`)
      over a **throwaway clone**: the reviewer's repository-wide grant lands
      in a copy that is removed afterwards, and the only artefact imported
      back is `suggestions.md` — never through a symlink, in either
      direction, because that file is the one path across the boundary and
      therefore the only one worth attacking. Isolation by construction,
      where the earlier post-run `git status` check could be passed by a
      payload that executed and then reverted itself. It also refuses a
      dirty tree (everything but `suggestions.md` must be committed),
      because the clone holds only commits and a reviewer reading less than
      the PR carries is a review of something else.

      A clone rather than a worktree because a worktree's `.git` points back
      into this checkout — the one path the container must not mount. **Docker
      is required**; without it the helper exits 7 rather than falling back to
      the host.

      That is about the copy the container reads, not about where this run
      lives: since step 1, `/ship` normally runs **inside** a worktree, and
      cloning out of one works — checked, not assumed, and the clone comes out
      on the branch. The two uses of the word sit close enough together to
      trip over, so it is worth reading twice before concluding the helper
      cannot run here.

      **Exit 12 is out of usage limits, and it means skip — not fail.** The
      helper preflights the selected auth against Grok's limits before the
      review, and a rate-limited or quota-exhausted team is not a defect in the
      branch: on exit 12 **skip the Grok loop for now and move to step 6**,
      reporting the round as skipped rather than clean or failed — a review the
      limits will not allow did not run, and neither a clean verdict nor a stop
      may be minted from it. It is the one non-zero exit that does not halt the
      chain; every other non-zero exit is the loop not having run and stops
      it. Note in the report that the Grok half was skipped on limits so a later
      `/ship` re-enters it.

      A skip can land mid-cycle: when a recheck is owed after a triage,
      `suggestions.md` is still on disk, and exit 12 there skips the recheck,
      not just a fresh pass. Proceed to step 6 all the same — the findings the
      file records are already triaged and fixed by then, and stalling the
      chain on the verification the limits refuse is the failure this exit
      exists to avoid — but the file stays where it is as the record of the
      unfinished half, the report says a recheck is owed rather than merely
      skipped, and every commit while it sits there stays scoped, exactly as
      the resume table requires.

      Residual, stated in the script and in `CLAUDE.md`: **egress is not
      restricted**. The container reaches the network, and confining it to
      `api.x.ai` needs an allow-list proxy Docker cannot supply alone. The
      credential half — no `gh` token, no SSH keys, no host filesystem — is
      closed. The reviewer also has **no .NET SDK**, so `dotnet test` is the
      host's gate, not the review's; the licence gate is stdlib Python and
      runs inside.

      Inside the copy, Grok discovers `.claude/commands/review-branch.md`
      itself, and that command owns the `suggestions.md` lifecycle: a full
      pass writes the file when findings remain, a recheck re-verifies an
      existing file and removes it when everything is resolved. Do not
      write or delete `suggestions.md` from here.

   2. **Check for `suggestions.md` at the repo root.** Absent → that is **one**
      clean pass, not the end: if the pass before it was also clean the loop is
      done, and otherwise go back to (1) and run one more. Keep the count in
      the report, because "clean twice" and "clean once" are what separate
      convergence from a lull, and a Grok recheck of nothing costs a few
      minutes. Present → run `/review-grok`, which
      triages and fixes — **its tool grant deliberately stops short of
      committing**. Then rerun the step 2 checks that apply to what it
      changed: a review fix is still an edit, and committing it unchecked
      hands the next reviewer a broken branch. Then `/commit` **scoped to
      the paths the triage touched** — `suggestions.md` is still on disk
      here by design, waiting for the next pass to recheck and remove it,
      and `/commit`'s unscoped form sweeps untracked files, which would
      commit the review record itself. Push the branch by name so the next
      Grok pass (and the PR) reads the fixed state, and go back to (1).

   One exit short of clean, reported rather than looped past — and one row
   that used to be a second:

   - **A `Needs a decision` row** from `/review-grok` no longer stops
     anything. That status exists because the finding is a judgement, and this
     chain now makes the judgement: take the option the surrounding argument
     supports, **write the answer into the resolution record beside the row**
     so the reasoning outlives the run, and continue to the recheck. The row
     is reported with the option taken and the option rejected. What must not
     happen is the quiet version — a row silently reclassified as `Fixed`,
     which loses both the question and the answer.
   - **Two consecutive clean rounds end it; twelve rounds is the ceiling.**
     Two clauses, and the first is deliberately *two* — **in this loop only**.
     Clean here means a pass that leaves no `suggestions.md` — a full review
     with nothing to write, or a recheck that removes the file; step 6 states
     its own clean in its own vocabulary and ends on one of them, by decision,
     with the cost named where the rule is. One clean round is not
     convergence: PR-11's Copilot round eight was clean and every round after
     it found more, so a rule ending on the first clean pass would have
     stopped at exactly the round that proves it should not. Requiring two
     also subsumes "never end on a round that produced a fix", since a round
     with findings is not clean and resets the count.

     Failing that, stop at twelve and hand over what survives — saying plainly
     that the loop ended on its ceiling rather than on convergence, because
     those are different states and only one of them is evidence.

     **Step 5's twelve is a count of Grok checks per PR, not per session** —
     this loop and step 6 each carry a twelve of their own. Every
     `grok-review.sh` invocation is one check — a full review and a recheck
     count the same — and this loop's ceiling is **no more than twelve of them
     against one PR**, carried across resumed `/ship` runs rather than reset
     each time the chain re-enters. A skip on limits (exit 12) is not a check
     and does not count; a review that ran and reported does. The ledger
     writes **before** the model call, not after:

     ```bash
     bash .claude/scripts/grok-ledger.sh <n> reserve <N> <full|recheck>
     ```

     then invoke the review helper. A reservation is an election, not just a
     write: two resumed runs can read the same count and claim the same slot,
     so the helper settles it after posting — the earliest comment for the
     slot wins, and a losing claim exits 4 having spent nothing. Losing
     means a concurrent `/ship` is mid-check on this PR, so stop the loop
     and say so — never reserve the next slot instead: two Grok runs share
     one root `suggestions.md`, and the later finisher would overwrite the
     earlier's findings or pass off its rival's clean pass as its own
     convergence. The two orders fail in
     opposite directions and only one is safe — written after, an
     interrupted run has spent the check and left no record, and the
     resumed run spends a
     thirteenth; written before, the worst case is a reservation for a check
     that never ran, which wastes one of the twelve and never exceeds it.
     Exit 12 is the one outcome that posts a second line —
     `grok-ledger.sh <n> release <N>` — because a skip is not a check; every
     other outcome lets the reservation stand as the record. A resumed run
     reads the count with `grok-ledger.sh <n> count`, which accepts only the
     ledger's line shapes from write-verified authors and counts an
     unreleased reservation as spent. The ledger goes through its own fixed
     helper for the same reason the Copilot request does: a
     `Bash(gh pr comment:*)` grant would also license `--edit-last`,
     `--delete-last` and `--repo` —
     editing history and writing across repositories — where the helper can
     post exactly the two lines above to a PR of this repository, and is
     edit-denied to the session that invokes it. Keep the running count in
     the report as well — the report line is for the reader, the ledger is
     for the machine — and when the twelfth is spent, stop and say the PR
     reached its Grok ceiling. When the loop ends clean instead, say so on
     the ledger — `grok-ledger.sh <n> converge <N>` — because a resumed run
     reading bare spend at the ceiling cannot tell convergence from
     exhaustion, and the difference is whether it reports the Grok half
     finished or blocked.

     The ceiling — then one number shared by both loops, as its size still
     is — was three, and three was wrong. By its seventh Copilot round
     PR-11's findings had gone 10 → 4 → 3 → 1 → 1 → 3 → 1, every one accepted,
     and rounds four through seven caught a documented-but-unenforced
     constraint, an assertion that could not fail in one direction, and a
     fail-open in the script's own manifest check — three defects three rounds
     would have shipped. The loop then ran past twelve and kept finding things,
     including a *clean* round eight after which every further round found
     more. A ceiling
     is for a reviewer and a triager **disagreeing**, which converges or never
     does; it was never for a loop still finding real things.

   A grok invocation that fails outright — not installed, not authenticated,
   the command not found — is reported as the loop not having run, never
   silently skipped and never substituted with a self-review. The exit-12
   limits skip above is the one deliberate exception, and it is not silent: it
   is reported as skipped-on-limits and proceeds to step 6. Every other
   outright failure mints nothing and stops the chain.

6. **The Copilot loop.** Once the Grok loop ends clean — or was reported
   skipped-on-limits this run, which hands over without being evidence of
   Grok convergence — hand the branch to the second reviewer and alternate
   the same way:

   1. **Request GitHub's Copilot review** on the PR:

      ```bash
      bash .claude/scripts/copilot-request.sh <n>
      ```

      The helper removes the reviewer, then re-adds it — after a landed
      review a plain POST enters a stale-reviewer state where the API
      returns the PR object and registers nothing, observed live four times
      in a row, while delete-then-post registered immediately. Its endpoint,
      method and body are fixed and its one parameter is shape-checked,
      which is why the frontmatter grants the *helper* and not `gh api`: a
      `Bash` rule matches a command prefix, so any raw-`gh api` grant —
      however narrow the path looks — still licenses method flags and
      payloads the deny rules never contemplated. The scripts under
      `.claude/scripts/` are the whole API surface this loop can touch, and
      they are **edit-denied to the session that runs them** —
      `.claude/settings.json` denies `Edit(.claude/scripts/**)`, so a
      granted name means the helper as reviewed, and widening one is a
      human's edit to a reviewed file, made with the deny lifted. (The deny
      is defence in depth, like the push rules: `Bash` redirection can still
      write a file, and no prefix list enumerates every spelling of write.
      What it removes is the quiet path — the session's own editing tools.)

      (For the curious: the request target accepts both `Copilot` and
      `copilot-pull-request-reviewer[bot]`; the finished review's *author*
      reads `copilot-pull-request-reviewer` from GraphQL and gains the
      `[bot]` suffix in REST; and `gh pr edit --add-reviewer` cannot resolve
      the bot at all — the fixed REST call inside the helper is the only
      door.)

      **A success exit is still not a registered request** — the only proof,
      on any round, is a new `review_requested` event on the issue timeline:

      ```bash
      bash .claude/scripts/copilot-request-count.sh <n>
      ```

      Request, verify the count grew, and on a silent drop retry with a
      minute-plus backoff. A request that will not register after ~10
      minutes of that stops the loop and says so: never wait on a review
      whose request never took, and never call the branch clean because
      asking failed.

      **The review's depth is not a request parameter, and the loop checks
      what it got.** The effort level is a repository admin setting —
      Settings → Copilot → Code review → **Review effort level**, two tiers,
      rendered in the timeline as "lite" and "balanced" — and no API field
      carries it: the REST event and the GraphQL types were introspected
      live and hold nothing. Left unset, GitHub routes by content, and the
      routing was observed doing exactly what its changelog implies: a
      27-file C# PR drew balanced, a six-file docs PR drew lite.

      So each round, read the tier from the PR timeline's own wording
      ("requested a *lite* review") and put it in the report. **A clean
      verdict at lite is weaker evidence than a clean verdict at balanced**,
      and on a branch that wanted scrutiny it is a prompt to pin the repo's
      effort level, not a pass to celebrate quietly — say which tier
      reviewed, every round, so the difference is never discovered from a
      merge regret.

   2. **Wait for the review to land** — a new review by
      `copilot-pull-request-reviewer` newer than the request. (That is the
      login GraphQL reports and the one `gh pr view --json reviews` filters
      on; REST spells the same account `copilot-pull-request-reviewer[bot]`.
      Both are the finished review's author — neither is the request
      target.) It takes minutes, and a clean one still posts (with zero
      comments), so landing is observable either way.

   3. **Count the new findings — suppressed ones included.** A review that
      "generated no new comments" can still carry a `Suppressed comments`
      block, and `/review-copilot` reads those on the same bar as inline
      threads; every real finding against this command's own machinery
      arrived suppressed. **Zero findings in the review is necessary, not
      sufficient**: a resumed run can carry an `Ask` thread from an earlier
      round that a fresh clean review never repeats, so before declaring the
      loop done, list the PR's unresolved review threads with
      `bash .claude/scripts/pr-review-threads.sh <n>` — an unresolved
      `Ask` is answered, resolved and reported rather than left, and any other
      unresolved thread is triage the loop still owes.

      **Zero findings and zero unresolved threads is all-resolved, and the
      loop ends there.** Do not request another review to confirm it: this
      loop stops on the first clean round, and a second request would be
      asking a question the landed review has already answered on the PR.
      Record the state by naming, in the report, the review that carried it
      and the `commit` oid it read — that oid against the pushed head is what
      a later `/ship` reads back, per *Resume, don't restart*, and with the
      no-newer-request clause stated there it is the whole of the marker.
      Nothing is posted to the PR to say so; the review itself is the record,
      which is more than the Grok half has.

      **If an optional extra round was requested, the loop is not done until
      it lands.** The paragraph below allows one on a branch that wanted
      scrutiny, and a request in flight is precisely the state the resume
      clause refuses to read as all-resolved — so having asked for it, wait
      for it and judge on that review, rather than declaring the state from
      the round before.

      **All of that weight now sits on the definition of clean, so read it
      strictly.** Clean is three things at once: no inline comments, an empty
      or absent suppressed block, and no unresolved threads. The last two are
      the ones that get skipped, and both have been — PR-11 posted "generated
      no new comments" above a suppressed finding worth fixing on rounds four,
      five and six, and under this rule each of those rounds would have ended
      the loop had the block gone unread. A second round used to be the net
      under that mistake and no longer is.

      **Anything short of all three is a round with findings.** Run
      `/review-copilot` **paused at its marker step**: let it
      triage and fix, then — because its tool grant cannot commit, and a
      `done` marker claims a committed fix — rerun the applicable step 2
      checks, `/commit` **scoped to the paths the triage touched**, and only
      then let it post its markers and resolve the threads. The scope is
      load-bearing, not habit: after a mid-cycle limits skip,
      `suggestions.md` is still on disk through this loop, and the unscoped
      form sweeps untracked files — committing the review record is exactly
      what the resume table forbids. Push the branch by name so the next
      request reviews the fixed state, and go back to (1).

   **This loop does not share step 5's stopping condition, and the asymmetry
   is the point rather than an oversight.** It ends on the **first** clean
   round, marked all-resolved, where step 5 still wants two.

   **An `Ask` thread is answered here rather than left open**, which is a
   change to what `/review-copilot` does on its own. That command leaves one
   open by design, because an unresolved thread is how a genuine ambiguity
   reaches a person; this chain has nobody to reach, so it decides, posts the
   decision and the rejected alternative **as a reply on the thread**, marks it
   `done`, and resolves it. The reply is the whole of what replaces the
   interruption — resolving without it destroys the question rather than
   answering it.

   The mechanical reason is worth knowing too: all-resolved is defined over
   *no unresolved threads*, so an `Ask` left open by round three is still open
   at round eleven. Left alone it does not stop this loop once — it stops it
   every subsequent round, and the loop runs to its ceiling with nothing new
   to fix. The ceiling is unchanged:
   twelve requested-review rounds per PR, counted from the timeline's
   `review_requested` events, the ones `copilot-request-count.sh` already
   proves each request by, so a resumed run recovers the count with no ledger
   at all. The outcomes are recoverable the same way — the landed reviews
   carry their comments and suppressed blocks and the thread list its
   unresolved threads — so a run that finds itself at the ceiling reads the
   last landed review before declaring the loop unconverged; the count alone
   cannot say which it was. A request that registers no review inside a
   reasonable wait is reported as the loop not having finished, never marked
   clean by timeout.

   **The cost of stopping at one is on the record, and it is Copilot's own.**
   This loop's findings arrive in the suppressed block long after the inline
   ones dry up, and they do not taper the way a disagreement does: PR-11's
   round eight came back clean and every round after it found more, which is
   the case a second round was there to catch and this rule gives up. What
   carries the weight instead is the strict definition of clean above — inline,
   suppressed and threads, all three — and the ceiling behind it. So the loop
   is now fast where it was thorough, and the one way to make that a bad trade
   is to read "generated no new comments" as the verdict rather than opening
   the block underneath it.

   Two rounds is still available and costs one line: request another before
   declaring all-resolved on a branch that wanted scrutiny — a lite-tier
   review of a large change is exactly that branch. Say in the report that you
   did, because a loop that ran longer than its rule is as much a departure as
   one that ran shorter.

7. **Merge, then tear the workspace down.** Both loops have finished — clean,
   all-resolved, skipped on limits, or unconverged at a ceiling — and the goal
   of this chain is a merged PR, so it merges.

   **Unconverged is not a reason to hold the PR.** A ceiling is a budget
   running out, not a verdict, and a branch that is green, reviewed and
   mergeable does not become less so because the reviewer had more to say.
   Report the state plainly — findings per round and whether the rate was
   still flat when the budget ran out is the useful signal — and merge.

   Two things genuinely gate it, and neither is a judgement:

   ```bash
   gh pr view <n> --json state,mergeable,mergeStateStatus
   gh pr checks <n>
   ```

   `mergeable` must be `MERGEABLE` and every check must pass. **A merge onto a
   red `main` is not a recommended option**, and a conflicted branch is a
   question about the caller's tree that this chain cannot answer. Either one
   stops here and is reported as what it is.

   **CI runs on the head commit, not on the PR**, so check the oid: a review
   round that pushed a fix invalidates the previous run, and `gh pr checks`
   reporting green for a commit that is no longer the head is the same
   stale-artefact trap step 6's `commit` oid exists for. Wait for the run on
   the pushed head rather than reading whichever finished last.

   Then merge with a merge commit, which is this repository's shape — every
   entry in `git log --merges` reads `Merge pull request #n from …`:

   ```bash
   gh pr merge --merge <n>
   ```

   **The flag comes before the number, and that is about the grant rather than
   about `gh`.** The frontmatter permits `Bash(gh pr merge --merge:*)`, and a
   permission rule is a prefix match — `gh pr merge <n> --merge` does not start
   with it and is simply denied. `gh` itself accepts either order (cobra
   intersperses flags and positionals, checked rather than assumed), so writing
   it flag-first costs nothing and keeps the narrow grant usable.

   `--squash` and `--rebase` are not alternatives to choose between here. The
   commits are the argument — `/commit` splits them so a reviewer can accept
   one and reject the next, and `/pr` writes its body from them — so squashing
   discards the thing two earlier steps spent their effort producing.

   **The merge is `gh`'s, not a push.** `.claude/settings.json` denies every
   push to `main` and that deny is untouched: the branch is merged on the
   remote by the API, and this checkout learns about it from `git fetch`. A
   chain that satisfied the goal by pushing to `main` would have defeated the
   rule rather than complied with it.

   Then put the workspace back the way step 0 wants to find it. Not step 0's
   block repeated — **the switch and the remove read differently depending on
   which outcome step 1 produced**, and the pull and the prune do not.

   ```bash
   bash .claude/scripts/git-switch-existing.sh main     # HEAD must be main to pull it
   git pull --ff-only                                   # main, with the merge commit on it
   git worktree remove ../<checkout-name>-<slug>        # only when the run forked one
   git worktree prune
   ```

   Leave the session in the main checkout. `ExitWorktree({action: "keep"})`
   first when the run happened in a worktree — the same `keep`, for the same
   reason as step 0, and the `git worktree remove` afterwards is what actually
   removes it.

   **Two of the four vary, and they are the first and the third.** The switch
   is always *invoked* and does different work: after a fork the session is
   already on `main` and it is a no-op, while after an in-place branch it is
   the only thing that makes the pull mean `main` rather than a feature branch.
   The remove is **fork only** — an in-place run has no worktree, and running
   it anyway exits non-zero and stops the chain on a helper failure with
   nothing behind it.

   The pull and the prune are unconditional, once the switch has put HEAD on
   `main`. Reading the pull as conditional is the mistake that leaves the main
   checkout a merge behind on the forked path, which is precisely the state
   step 0 exists to prevent the next run from starting in.

   **Verify the merge before tearing anything down**, and verify it from the
   remote rather than from the exit code:
   `gh pr view <n> --json state,mergeCommit` must read `MERGED` with an oid. Removing the worktree is the one step in
   this chain that destroys something, and doing it on an assumed merge is how
   an unmerged branch loses its only checkout.

   The merged branch itself stays. `git branch -d` is denied, deliberately, and
   a merged branch costs a line in `git branch` — name it in the report.

## Report

**Open with the workspace**: the worktree this run happened in and the branch
it holds, or the main checkout and why no worktree was forked. It is the one
line that tells a reader where every path in the rest of the report is rooted,
and a resumed run reports it whether or not this run created it.

Then one line per step: done, skipped and why, or stopped and what is needed —
including the push, which reports which of its three states it found even when
that state was "nothing to do". Each review loop reports one line per round —
findings raised, findings fixed, and what each round pushed — its running
check count against its twelve (the PR carries the durable copy: step 5's
ledger comments, step 6's timeline events; the report line is the
human-readable echo), and how it ended, in that loop's own vocabulary: step 5
clean, skipped on limits with re-entry owed, or stopped unconverged; step 6
**all-resolved, naming the review and the `commit` oid it read**, or stopped
unconverged. Neither list has an ending that means "a finding stopped us" any
more — a decided row and an answered `Ask` belong in the decisions section
below, and filing one as a stop is the silent-decision failure this report
exists to prevent. The oid is not
decoration — it is the whole of step 6's marker, and a later `/ship` compares
it against the pushed head to decide whether that loop is owed at all.

**Then the decisions.** Every place this chain answered a question that used to
stop it gets a line: the check finding it reconciled and which side won, the
`Needs a decision` row and the option rejected, the `Ask` thread and what was
posted on it. This is the section that replaces the interruption, so a run that
took decisions and lists none of them has not reported — it has hidden. A run
that took none says so in one line.

**Then the merge and the workspace.** Whether the PR merged and its merge oid,
or which of the two gates stopped it; that `main` was pulled and is now at that
oid; the worktree removed, or the one left behind and why git refused it; and
the merged branch still sitting in `git branch`.

A step skipped on an assumption gets its assumption restated here rather than
left in the middle of the run, and a check that did not run is named. The whole
value of chaining these commands is that the summary is still honest about each
one — and now that nothing stops for a person, the report is the only place a
person finds out what was decided on their behalf.
