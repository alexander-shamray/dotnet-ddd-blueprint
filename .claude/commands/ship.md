---
description: Branch, commit and open a PR in one pass, stopping only where a human must act
argument-hint: "[what the change does] — omit and each step derives its own"
allowed-tools: Read, Grep, Glob, Write, Skill, Bash(git status:*), Bash(git diff:*), Bash(git branch:*), Bash(git log:*), Bash(git fetch:*), Bash(git checkout -b:*), Bash(git switch -c:*), Bash(git rev-parse:*), Bash(git add:*), Bash(git commit:*), Bash(git reset HEAD:*), Bash(wc:*), Bash(gh pr create:*), Bash(gh pr view:*), Bash(gh pr list:*)
---

Take the working tree from wherever it is to an open PR. Description:
$ARGUMENTS — if empty, each step derives its own.

## This command owns no rules

`/branch`, `/commit` and `/pr` hold the branch-naming table, the
commit-splitting test and the PR body form. **Load each and follow it. Do not
restate them here** — a fourth copy of the naming table is exactly the drift
this repo exists to close, and a chainer that paraphrases the steps it calls is
the worst place for that copy to live.

What this command adds is the handoffs: which steps are still owed, and what to
do at the two places the sequence cannot continue on its own.

## It cannot run to the end

`git push` is denied to Claude in `.claude/settings.json`. `/pr` already stops
for it and prints the line to run; that stop is inherited here, not worked
around. Do not let `gh pr create` offer to push the branch instead.

The stop is the point of the command rather than a limitation of it: it is the
last moment where the work is still cheap to change. Re-run `/ship` after
pushing and it picks up where it left off.

## Resume, don't restart

**Read the state first and run only what is still owed.** Every step is
skippable because an earlier run already did it:

| State | What is owed |
|---|---|
| On `main` | All of it — start at step 1 |
| On a branch, tree dirty | Checks, `/commit`, `/pr` |
| On a branch, tree clean | `/pr` — which stops for the push if the branch is unpushed or ahead |
| On a branch with an open PR | Nothing. Say so and stop — `/pr` treats this as an update, and updating is a decision, not a default |

`git status -sb`, `git branch --show-current` and `gh pr list --state open`
answer all four. Read them before doing anything.

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

4. **`/pr`**. It derives its own title from the commits; $ARGUMENTS described
   the branch, not the PR.

   This is where the push stop lands. If `/pr` stops, that is the chain
   finishing correctly — report it as a stop, not a failure.

## Report

One line per step: done, skipped and why, or stopped and what is needed. End
with the PR URL, or with the single command the user has to run to get past the
current stop.

A step skipped on an assumption gets its assumption restated here rather than
left in the middle of the run, and a check that did not run is named. The whole
value of chaining four commands is that the summary is still honest about each
one.
