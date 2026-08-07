---
description: Branch, commit, push and open a PR in one pass, then loop the external review until it comes back clean
argument-hint: "[what the change does] — omit and each step derives its own"
allowed-tools: Read, Grep, Glob, Write, Skill, Bash(git status:*), Bash(git diff:*), Bash(git branch:*), Bash(git log:*), Bash(git fetch:*), Bash(git checkout -b:*), Bash(git switch -c:*), Bash(git rev-parse:*), Bash(git add:*), Bash(git commit:*), Bash(git reset HEAD:*), Bash(git push -u origin:*), Bash(git push origin:*), Bash(wc:*), Bash(gh pr create:*), Bash(gh pr view:*), Bash(gh pr list:*), Bash(grok:*)
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
for anyone — and the PR is no longer where it stops. Step 5 keeps going: the
external reviewer reads the branch, `/review-grok` triages what it found, and
the chain ends only when a review pass leaves nothing behind. **Step 2 and a
`Needs a decision` finding in step 5 are the only things that stop it** — a
check that finds something halts the run and hands the finding back, because
fixing it is the user's call.

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
| On a branch, tree clean and pushed | `/pr`, then the review loop |
| On a branch with an open PR | The review loop (step 5) — and, if the tree is dirty, checks, `/commit` and a push first, so the reviewer reads what the PR will actually carry |
| On a branch with an open PR and no `suggestions.md` after a clean pass | Nothing. Say so and stop |

`git status -sb`, `git branch --show-current`, `gh pr list --state open` and
a look for `suggestions.md` answer all six. Read them before doing anything.

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
      grok -p "/review-branch" --permission-mode acceptEdits
      ```

      Grok discovers `.claude/commands/review-branch.md` itself, and that
      command owns the `suggestions.md` lifecycle: a full pass writes the
      file when findings remain, a recheck re-verifies an existing file and
      removes it when everything is resolved. Do not write or delete
      `suggestions.md` from here.

   2. **Check for `suggestions.md` at the repo root.** Absent → the loop is
      done; the review came back clean. Present → run `/review-grok`, which
      triages, fixes, and commits — then push the branch by name so the next
      Grok pass (and the PR) reads the fixed state, and go back to (1).

   Two exits short of clean, both reported rather than looped past:

   - **A `Needs a decision` row** from `/review-grok` stops the loop — that
     status exists because the finding is the user's call, and a loop that
     keeps running past it buries the one thing that needed a human.
   - **Three rounds without convergence** stops the loop. A reviewer and a
     triager still disagreeing after three exchanges are not going to settle
     it between themselves; hand over the surviving findings instead of
     grinding tokens against them.

   A grok invocation that fails outright — not installed, not authenticated,
   the command not found — is reported as the loop not having run, never
   silently skipped and never substituted with a self-review.

## Report

One line per step: done, skipped and why, or stopped and what is needed —
including the push, which reports which of its three states it found even when
that state was "nothing to do". The review loop reports one line per round —
findings raised, findings fixed, and what each round pushed — and how it
ended: clean, stopped on a decision, or stopped unconverged. End with the PR
URL.

A step skipped on an assumption gets its assumption restated here rather than
left in the middle of the run, and a check that did not run is named. The whole
value of chaining five commands is that the summary is still honest about each
one.
