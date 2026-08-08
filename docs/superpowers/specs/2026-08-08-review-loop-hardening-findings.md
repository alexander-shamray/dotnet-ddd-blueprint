# Review-loop hardening — the findings this branch exists to resolve

Collected from Copilot's eleventh review of PR #12
(`feat(template): service skeleton and architecture test gate`), the round
whose findings were `Ask`-class — trust-model questions rather than branch
defects — and stopped the review loop under its own rule. PR #12 merged with
these threads deliberately left open; this branch is where they get answered.
Frozen at write time, like every record in this directory.

## Context

PR #12's review loop hardened its own machinery across eleven rounds: raw
`gh api:*` grants became `repos/` prefixes, prefixes became six
fixed-endpoint parameter-validated helpers under `.claude/scripts/`, and the
helpers' own first review closed a cross-repository mutation hole, a
per-page count bug and a truncated thread map. Round eleven then named the
limit of that whole ladder: the harness itself.

## The findings

### 1. Self-modification defeats the helper grants (inline, thread open)

PR #12 comment `3739311705` (`.claude/commands/ship.md`): the command retains
repository-wide `Write`, so a prompt-injected session can rewrite
`copilot-request.sh` and invoke the modified script through its granted
name — "review content or prompt injection can turn the supposedly fixed
endpoint into any authenticated `gh api` mutation. Remove write access while
these helpers are callable, or execute immutable helpers outside the
writable checkout."

### 2. The same, from the triage side (inline, thread open)

PR #12 comment `3739311731` (`.claude/commands/review-copilot.md`): "These
helpers are not actually a narrow API surface while this command also has
unrestricted `Edit` … Because PR comments are untrusted input, this restores
arbitrary authenticated shell/API behaviour despite removing `gh api:*`.
Make the helpers immutable to this command or separate editing fixes from
the privileged reply/resolve phase."

### 3. The worktree guard does not sandbox Grok (suppressed, review 11)

`.claude/commands/ship.md`, the step 5 guard: "`review-branch` grants
repository-wide edits plus `dotnet build`/`dotnet test`, so injected
instructions can add executable MSBuild/test code, run it with the host's
credentials, and revert the edit before this check; `git status` would then
be clean after the side effect already occurred. Run Grok in an isolated
checkout/container without credentials or network access and import only its
review output."

## Why these stopped the loop instead of being fixed in place

All three are true of the harness, not of one file: any session holding
`Edit` plus any Bash grant can rewrite what it then runs — including the
command files themselves — and an external reviewer given edit and build
rights is code execution by design. The fixes are infrastructure (immutable
tooling outside the writable tree, split-privilege phases, a credential-free
isolated checkout for the reviewer), which is a scope decision no PR should
smuggle in through a review reply. PR #12's loop rule for exactly this class
is `Ask`: leave the thread open, put it in the report, hand it to a human —
which is how it reached this branch.

## What resolving them likely means

Not a commitment — the design work is this branch's job. The shapes the
findings themselves point at:

- **Immutability**: helpers executed from a location the session's `Edit`
  cannot reach (outside the checkout, or a read-only copy verified by hash
  before invocation), so a grant on the helper's name means the helper as
  reviewed.
- **Phase separation**: the triage phase (needs `Edit`, needs no API writes)
  and the reply/resolve phase (needs API writes, needs no `Edit`) never hold
  both capabilities at once.
- **Reviewer isolation**: Grok reviews a copy — a worktree or container with
  no credentials and no network beyond what a review needs — and only
  `suggestions.md` crosses back.
