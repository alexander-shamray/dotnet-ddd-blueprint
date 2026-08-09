---
description: Branch, commit, push and open a PR in one pass, then loop the external reviews — Grok, then Copilot — until both come back clean
argument-hint: "[what the change does] — omit and each step derives its own"
allowed-tools: Read, Grep, Glob, Write, Skill, Bash(git status:*), Bash(git diff:*), Bash(git branch:*), Bash(git log:*), Bash(git fetch:*), Bash(git checkout -b:*), Bash(git switch -c:*), Bash(git rev-parse:*), Bash(git add:*), Bash(git commit:*), Bash(git reset HEAD:*), Bash(git push -u origin:*), Bash(git push origin:*), Bash(wc:*), Bash(gh pr create:*), Bash(gh pr view:*), Bash(gh pr list:*), Bash(bash .claude/scripts/copilot-request.sh:*), Bash(bash .claude/scripts/copilot-request-count.sh:*), Bash(bash .claude/scripts/grok-review.sh), Bash(sleep:*)
---

Take the working tree from wherever it is to an open PR. Description:
$ARGUMENTS — if empty, each step derives its own.

## This command owns no rules

`/branch`, `/commit` and `/pr` hold the branch-naming table, the
commit-splitting test and the PR body form. **Load each and follow it. Do not
restate them here** — a fourth copy of the naming table is exactly the drift
this repo exists to close, and a chainer that paraphrases the steps it calls is
the worst place for that copy to live.

What this command adds is the handoffs: which steps are still owed, and where
the sequence is allowed to stop.

## It runs to the end

`/pr` pushes the branch itself, so the chain reaches an open PR without waiting
for anyone — and the PR is no longer where it stops. Steps 5 and 6 keep going:
Grok reads the branch and `/review-grok` triages what it found, then Copilot
reads the PR and `/review-copilot` triages that, and the chain ends only when
both reviewers have nothing left to say. **Step 2, a `Needs a decision`
finding in step 5 and an `Ask` thread in step 6 are the only things that stop
it** — a check that finds something halts the run and hands the finding back,
because fixing it is the user's call.

That is a real change in character and worth naming. Under the old blanket
`Bash(git push:*)` deny this command could not finish: it stopped before the
push, and that stop was the last cheap moment to change the work. The narrow
denies that replaced it — `--force`, `--delete`, any push to `main` — keep
the cases that are decisions rather than steps. Everything else now proceeds,
which means **the checks are carrying the weight the stop used to.** Skipping
them to save a minute is no longer a small thing.

## Resume, don't restart

**Read the state first and run only what is still owed.** Every step is
skippable because an earlier run already did it:

| State | What is owed |
|---|---|
| On `main` | All of it — start at step 1 |
| On a branch, tree dirty | Checks, `/commit`, push, `/pr` |
| On a branch, tree clean, unpushed or ahead | Push, `/pr` |
| On a branch, tree clean and pushed | `/pr`, then the review loops |
| On a branch with an open PR | The review loops (steps 5–6), Grok before Copilot — and, if the tree is dirty, checks, `/commit` **scoped to the implementation paths** and a push first, so the reviewers read what the PR will actually carry. Never unscoped while `suggestions.md` is on disk: that file is Grok's working state, and the unscoped form sweeps untracked files into the commit |

**A loop's clean state cannot be read from the tree**, so a resumed run
re-enters both loops rather than inferring they ran: `suggestions.md` is
absent before the first review and after a clean one, and the two states are
indistinguishable. Re-entering is safe because both loops are idempotent
against a clean branch — a Grok full review of nothing writes nothing, and a
requested Copilot review posts with zero comments — and that re-run is the
proof, where the inference was a guess. The only "nothing owed" state is the
one this run just produced by watching both loops end clean.

`git status -sb`, `git branch --show-current`, `gh pr list --state open` and
a look for `suggestions.md` (it decides recheck versus full review inside
step 5, not whether step 5 runs) answer all five. Read them before doing
anything.

## Steps

1. **`/branch`**, passing $ARGUMENTS. Skip if already off `main`.

   `/branch` stops when it is already on a branch and asks whether this is a
   second change or a continuation. In a chain that stop is wrong — being on a
   feature branch is the normal state of a resumed `/ship`. Take the current
   branch as this change's branch and carry on, but **say that you assumed it**
   and name the branch, so a tree that has drifted onto the wrong one is visible
   before anything is committed to it.

2. **Checks** — `/validate-blueprint`, plus `/check-links` when the change
   touched links, cross-references or nav footers.

   **Before `/commit`, not after.** A defect found after the commit costs a
   second commit or a rewrite; found here it is an edit. This is also the step a
   chained workflow silently drops, which is why it is a step rather than a
   footnote: `/pr` requires the body to state whether these ran, so skipping
   them quietly makes the PR body untrue.

   Stop on a finding and report it. Fixing it is the user's call, not yours. If
   the user asks to skip the checks, skip them — and say so in the PR body.

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

   2. **Check for `suggestions.md` at the repo root.** Absent → the loop is
      done; the review came back clean. Present → run `/review-grok`, which
      triages and fixes — **its tool grant deliberately stops short of
      committing**. Then rerun the step 2 checks that apply to what it
      changed: a review fix is still an edit, and committing it unchecked
      hands the next reviewer a broken branch. Then `/commit` **scoped to
      the paths the triage touched** — `suggestions.md` is still on disk
      here by design, waiting for the next pass to recheck and remove it,
      and `/commit`'s unscoped form sweeps untracked files, which would
      commit the review record itself. Push the branch by name so the next
      Grok pass (and the PR) reads the fixed state, and go back to (1).

   Two exits short of clean, both reported rather than looped past:

   - **A `Needs a decision` row** from `/review-grok` stops the loop — that
     status exists because the finding is the user's call, and a loop that
     keeps running past it buries the one thing that needed a human.
   - **Twelve rounds** stops the loop. This bound was three, and three was
     wrong. By its seventh Copilot round PR-11's findings had gone
     10 → 4 → 3 → 1 → 1 → 3 → 1, every one accepted and fixed, and rounds four
     through seven caught a documented-but-unenforced constraint, an assertion
     that could not fail in one direction, and a fail-open in the script's own
     manifest check — three defects a three-round bound would have shipped.
     That was the evidence for raising it; the loop then ran on and kept
     finding things, including a round-eight *clean* pass followed by eight
     more findings. The bound exists for a reviewer and a triager
     *disagreeing*, which converges or never does; it was never meant to stop
     a loop still finding real things and fixing them in minutes. Twelve, and
     hand over what survives.

   A grok invocation that fails outright — not installed, not authenticated,
   the command not found — is reported as the loop not having run, never
   silently skipped and never substituted with a self-review.

6. **The Copilot loop.** Once the Grok loop ends clean, hand the branch to the
   second reviewer and alternate the same way:

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
      loop done, list the PR's unresolved review threads — an unresolved
      `Ask` stops the loop exactly as a new one would, and any other
      unresolved thread is triage the loop still owes. Zero findings and
      zero unresolved threads → the loop is done.
      Otherwise run `/review-copilot` **paused at its marker step**: let it
      triage and fix, then — because its tool grant cannot commit, and a
      `done` marker claims a committed fix — rerun the applicable step 2
      checks, `/commit`, and only then let it post its markers and resolve
      the threads. Push the branch by name so the next request reviews the
      fixed state, and go back to (1).

   The same two early exits as step 5, in this loop's vocabulary: an **`Ask`**
   thread — left open by `/review-copilot` by design — stops the loop, and
   **twelve rounds** stops it. A request that registers no review inside a
   reasonable wait is reported as the loop not having finished, never marked
   clean by timeout.

   **This is the loop the twelve is for.** Copilot's findings arrive in the
   suppressed block long after the inline ones dry up, and they do not taper
   the way a disagreement does: on PR-11 rounds four, five and six each posted
   "generated no new comments" above a suppressed finding that was worth
   fixing. Do not read a clean *inline* verdict as convergence, and do not
   stop early because the counts look small — a round costs minutes and the
   things it finds at that depth are the ones nobody else will.

## Report

One line per step: done, skipped and why, or stopped and what is needed —
including the push, which reports which of its three states it found even when
that state was "nothing to do". Each review loop reports one line per round —
findings raised, findings fixed, and what each round pushed — and how it
ended: clean, stopped on a decision or an open `Ask`, or stopped unconverged.
End with the PR URL.

A step skipped on an assumption gets its assumption restated here rather than
left in the middle of the run, and a check that did not run is named. The whole
value of chaining six commands is that the summary is still honest about each
one.
