---
description: Triage Copilot's review comments on a PR — verify each before acting on it
argument-hint: "[PR number — omit for the current branch's PR]"
allowed-tools: Read, Grep, Glob, Edit, Bash(gh pr view:*), Bash(gh pr list:*), Bash(gh pr diff:*), Bash(gh api repos/:*), Bash(gh api graphql:*), Bash(git log:*), Bash(git diff:*), Bash(git branch:*)
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
2. **Inline comments** — `gh api repos/:owner/:repo/pulls/<n>/comments`. Take
   `path`, `line`, `body` and `in_reply_to_id`, and skip any thread already
   answered by the repo owner.
3. **Issue comments** — `gh api repos/:owner/:repo/issues/<n>/comments`.

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
   `gh api repos/:owner/:repo/pulls/<n>/comments/<id>/replies -f body='…'`,
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
gh api graphql -f query='
query($owner:String!,$repo:String!,$pr:Int!){
  repository(owner:$owner,name:$repo){
    pullRequest(number:$pr){
      reviewThreads(first:50){
        nodes{ id isResolved comments(first:1){ nodes{ databaseId path } } }
      }
    }
  }
}' -F owner=<owner> -F repo=<repo> -F pr=<n>
```

`comments.nodes[0].databaseId` is the inline comment's numeric id from the REST
call above; `id` is the thread. Then, once the marker is posted:

```bash
gh api graphql -f query='mutation($id:ID!){
  resolveReviewThread(input:{threadId:$id}){ thread{ isResolved } }
}' -F id=<PRRT_…>
```

The mutation is idempotent — re-running it on a resolved thread returns
`isResolved: true` and changes nothing — so a re-run after a partial pass is
safe.

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
