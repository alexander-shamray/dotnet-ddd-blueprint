---
description: Triage Copilot's review comments on a PR — verify each before acting on it
argument-hint: "[PR number — omit for the current branch's PR]"
allowed-tools: Read, Grep, Glob, Edit, Bash(gh pr view:*), Bash(gh pr list:*), Bash(gh pr diff:*), Bash(bash .claude/scripts/pr-review-comments.sh:*), Bash(bash .claude/scripts/pr-comment-reply.sh:*), Bash(bash .claude/scripts/pr-review-threads.sh:*), Bash(bash .claude/scripts/pr-thread-resolve.sh:*), Bash(git log:*), Bash(git diff:*), Bash(git branch --list:*), Bash(git branch --show-current), Bash(git branch -a)
---

Work through the Copilot review on PR $1 — if empty, the PR for the current
branch.

## Fetch all three places comments hide

Copilot posts as `copilot-pull-request-reviewer[bot]`, and its inline comments
appear under the author `Copilot`. Collect:

1. **Review bodies** — `gh pr view <n> --json reviews`. The overview, and the
   `<details><summary>Suppressed comments</summary>` block, which holds findings
   that never surfaced as inline comments. Read the suppressed ones; they are
   not filtered for being wrong.
2. **Inline comments** — `bash .claude/scripts/pr-review-comments.sh <n>`.
   Take `path`, `line`, `body` and `in_reply_to_id`, and skip any thread
   already answered by the repo owner.
3. **Issue comments** — `gh pr view <n> --json comments`.

The scripts under `.claude/scripts/` are the whole of this command's API
surface, and that is the point: a `Bash` permission rule matches a command
prefix, so a raw `gh api` grant of any spelling licenses methods and payloads
nobody reviewed. Each helper fixes its endpoint and shape-checks its
parameters — and the scripts are **edit-denied to the session**
(`.claude/settings.json` denies `Edit(.claude/scripts/**)`), because PR
comments are untrusted input and a triager that could rewrite a helper
before invoking it would make the fixed endpoints a fiction. Widening one is
a human's edit, made with the deny lifted. The ordering discipline below is
the same defence on the time axis: the fixing happens first, and the
privileged reply/resolve calls run only after the commit exists.

A re-review supersedes an earlier one on the same line. Work from the latest.

## Verify before you act

**Copilot is often right and sometimes confidently wrong, and this repo has
already seen both.** On PR #1 it correctly caught a stray `.1` outside a
markdown link. On PR #2 it claimed `csharp_style_var_when_type_is_apparent`
does not cover the "RHS names the type" case; checking Roslyn's actual
behaviour showed it does, and the comment was answered with the evidence rather
than obeyed.

So for each finding, before changing anything:

- **Read the site.** Not the diff hunk — the file, with enough context to see
  what the surrounding passage is doing.
- **Check it against the repo's own rules.** A comment that asks for something
  `CLAUDE.md` settles — file-scoped namespaces, braceless single statements,
  explicit types over `var`, British prose beside real identifier spellings —
  is wrong by construction. Cite the rule and reject it.
- **Check the claim, not the confidence.** If it asserts how a tool or library
  behaves, verify that behaviour before agreeing. A bot's certainty is not
  evidence.
- **Grep the corpus.** If the finding is real, it is usually real in more than
  one place; the fix is every site, in one pass (`CLAUDE.md`, *the one rule
  that matters*).

## Classify each finding

| | |
|---|---|
| **Accept** | Real defect. Fix every site, not just the flagged one. |
| **Accept, wider** | Real, and the same shape exists elsewhere. Say how many. |
| **Reject — house rule** | Contradicts a settled choice. Name the rule. |
| **Reject — wrong** | The claim does not hold. Say what you checked. |
| **Ask** | Genuine design ambiguity. Surface it; do not pick silently. |

## Replying, marking and resolving

**Every thread you triage ends closed, and it ends closed in three steps.** A
thread left open reads as one nobody looked at, and the next reviewer — human
or bot — re-opens the same argument on the next PR.

1. **The reasoned reply.** Always for a rejection: a rejection nobody reads is
   a comment that comes back. For an acceptance, only where the commit does not
   already say it — a one-line fix needs no essay. Post with
   `bash .claude/scripts/pr-comment-reply.sh <n> <comment-id> '…'`,
   and only after showing the user the text.
2. **The marker**, as its own reply on the same thread, one word and nothing
   else: **`done`** if the finding was accepted and the fix is committed,
   **`rejected`** if it was not. It goes last so the thread's final line states
   the outcome without anyone reading the argument above it, and one word is
   greppable across a PR's history in a way a paragraph is not.

   `done` claims the work is committed. Post it after the commit exists, never
   before — a marker that runs ahead of the fix is worse than no marker,
   because it is the line a reviewer trusts instead of checking.
3. **Resolve the thread.**

### Resolving

**REST cannot do this** — `/pulls/<n>/comments` has no resolve field, and there
is no `gh pr` subcommand for it. It is a GraphQL mutation on a *thread* ID
(`PRRT_…`), which is not the comment's `id`, so the mapping must be fetched:

```bash
bash .claude/scripts/pr-review-threads.sh <n>
```

Each output line is `<thread-id> <isResolved> <comment-database-id> <path>`;
the database id joins to the inline comment's numeric `id` from the intake
step. Then, once the marker is posted:

```bash
bash .claude/scripts/pr-thread-resolve.sh <n> <PRRT-thread-id>
```

The PR number is not decoration: thread node ids are global, so the helper
refuses to run the mutation until it has seen the id in that PR's own
thread map.

The mutation inside the helper is idempotent — re-running it on a resolved
thread returns `true` and changes nothing — so a re-run after a partial pass
is safe.

**One verdict does not get this treatment: `Ask`.** A thread raising a genuine
design ambiguity is unresolved by definition, and closing it would hide the
question behind a green tick. Leave it open, with no marker, and put it in the
report instead.

## Report

A table of finding → verdict → sites touched, then the diff summary. State
the count you rejected and why, separately from the count you fixed — a review
where everything was accepted usually means the verification step was skipped,
and one where everything was rejected deserves the same suspicion.

Finish with the thread state: how many were marked `done`, how many `rejected`,
how many resolved, and — named individually — any left open as `Ask`.
