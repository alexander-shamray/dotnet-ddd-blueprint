---
description: Loop a defensive security audit up to seven rounds, filing a GitHub issue per confirmed medium-or-above finding, until a round surfaces nothing new
argument-hint: "[scope hint, e.g. 'the compose stack' or a path] — omit to sweep the whole repo"
allowed-tools: Read, Grep, Glob, Agent, Bash(gh issue list:*), Bash(gh issue view:*), Bash(gh issue create:*), Bash(gh label list:*), Bash(gh label create:*), Bash(gh repo view:*), Bash(git status:*), Bash(git log:*), Bash(git grep:*), Bash(grep:*)
---

Sweep the repository for security findings, file the real ones as GitHub
issues, and repeat until a round finds nothing new — a ceiling of **seven
rounds**. Scope: $ARGUMENTS — if empty, the whole repo.

## What counts as an issue

**Only a finding that is confirmed and not already tracked, at severity medium
or above.** Three gates, and each drops candidates the round must not file:

- **Confirmed.** A subagent's claim is raw data, never a filing. Read the code
  it cites yourself and reproduce the reasoning before it becomes an issue. An
  audit that files unverified agent output manufactures noise the next round
  then has to triage.
- **Medium or above.** Low and info findings are recorded in the round summary
  for the user to weigh, not filed. The threshold is the user's to move, not
  this command's.
- **Not already tracked.** Before filing, `gh issue list --state all` and read
  the open set — a finding that matches an existing issue, or a documented
  time-boxed decision in the code (a `closed by PR-NN` remark, an accepted risk
  named in a README), is **already tracked**. Re-filing it is the drift this
  repo exists to close, one issue tracker over.

A candidate that fails any gate is not a clean round's absence of findings — it
is a finding handled without a new issue. Say which in the summary.

## The round

Each round is the review done once, end to end:

1. **Fan out.** Spawn read-only audit subagents over disjoint areas so no two
   read the same tree — the natural cut is CI/tooling, the application source,
   and the deploy/infrastructure surface, but let the scope hint narrow it.
   Give each the same contract: report file, line, severity, the concrete
   exploit scenario (who controls the input, what happens), and a fix — as raw
   data, most severe first. Tell each what is **documented and deliberate** so
   it does not re-report accepted risks as defects.
2. **Verify.** For every medium-or-above candidate, read the cited code and
   confirm the scenario holds. Drop what does not survive.
3. **De-duplicate.** Check each survivor against the tracked set and the
   already-tracked rule above.
4. **File.** One issue per survivor, most severe first, in the house body form:
   a summary, the affected lines quoted, why it is exploitable, a fix, and the
   severity. Label `security` (create the label once if absent). End the body
   noting it came from an authorised review and was verified at filing.
5. **Summarise the round.** New issues filed (with numbers), candidates dropped
   at each gate and why, and the lows/infos recorded but not filed.

## Where it stops

**A round is clean when it files no new issue** — every candidate either failed
verification, sat below the threshold, or was already tracked. The loop stops
on a clean round or at the seventh, whichever comes first.

**One clean round is weaker evidence than it looks, and the ceiling is why it
is safe to stop on it anyway.** This repo has watched a review loop go clean and
then find more — PR-11's Copilot round eight came back clean and every round
after it surfaced findings, which is the whole reason its review ceiling moved
from three to twelve. A security sweep differs from that loop in a way that
makes a single clean round the right stop here rather than there: each round's
fan-out is **stateless** — it re-reads the tree from scratch, not a reviewer
reacting to the last round's fixes — so a clean round is a fresh full read that
found nothing, not a lull between exchanges. But the earlier rounds change the
tree only if the **user** acts on the filed issues between runs; this command
files and does not fix. So:

- **If issues from a prior round are still open and unfixed**, a later round
  re-finds them — and they are already tracked, so it files nothing and reads as
  clean while the exposure is still live. That is convergence of the *filing*,
  not of the *risk*. Say so plainly: "clean — but issues #NN, #MM remain open."
- **Stop at seven and hand over what survives** if the loop has not gone clean,
  stating that it ended on the ceiling rather than on convergence. Seven bounds
  a sweep that keeps turning up new areas; it is not a promise the repo is clean
  at seven.

**Never fail open.** A round that errored — a subagent that died, a `gh` call
that failed — is not a clean round. Report the error and let the user decide;
do not count a review that did not happen as a review that found nothing. This
is the same rule that made the Grok loop trust the verdict check over the exit
code: a review that never ran cannot report as clean.

## What this command does not do

It **files**; it does not **fix**. A security fix is a code change with its own
test and its own PR, and the delivery plan orders that work — this command's job
is to make the findings visible and tracked, not to edit source. If a finding is
better closed than tracked (a one-line binding, a stray secret), say so in the
round summary and leave the change to the user.
