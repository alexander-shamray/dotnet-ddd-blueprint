# Sandboxing the external reviewer

Design for `chore: run the external reviewer in a container`. Written before
implementation and frozen at write time — where this document and the code
disagree, the disagreement is the record of where the design moved.

Closes finding 3 of
`2026-08-08-review-loop-hardening-findings.md`, which PR-15 could not: Copilot
raised it there, the flag-only fixes were tried and failed, and the residual was
recorded in CLAUDE.md rather than closed.

## 1. What was actually wrong

`grok-review.sh` has run the reviewer in a disposable git worktree since it was
written, and that isolates **edits** and nothing else. The process still held
this host's filesystem, its network and its credentials. `gh` is authenticated
here, so a reviewer with a terminal could push to a remote that the session
which spawned it is denied — a deny list bounds the session, never the
subprocess it starts.

PR-15 tried to fix this with permission flags and could not. That attempt is
worth recording because the obvious answer is wrong:

- A `Bash(...)` allow rule **does** map onto grok's `run_terminal_command` —
  verified against a live `git status --short` in a mode that would otherwise
  have prompted.
- An allow-list still fails: `/review-branch` reaches for commands outside any
  list narrow enough to be worth having, and **each miss is a cancelled review,
  not a smaller one.**
- `dontAsk` beats `bypassPermissions` only in honouring deny rules. It
  constrains a model's mistake, not an adversary — anything holding a terminal
  can spell `git push` another way.

So the boundary has to be the process boundary.

## 2. The container is the grant

`.claude/sandbox/Dockerfile`: Debian, git, curl, a non-root `reviewer` user, and
grok installed by x.ai's own installer. What does **not** enter it is the
deliverable — no `gh` token, no SSH keys, no cloud profile, no host filesystem
beyond the one directory the review is about.

`bypassPermissions` stays, and stops being the risk it was. The blast radius is
the container, which is the entire reason to have one; arguing about tool grants
inside a box that holds nothing is arguing about the wrong boundary.

**A clone, not a worktree.** A worktree's `.git` is a file pointing back into
the host checkout — precisely the path the container must not mount — so git
inside would resolve nothing. The clone carries its own `.git` and its own
`origin/*` refs, which is all `/review-branch` reads.

## 3. Credentials: three files, all copies

`XAI_API_KEY` is the better path and is tried first. It needs no file, carries
no refresh token and none of the account holder's details, and grok falls back
to it when no session token is present — which a fresh container never has.

**But "set" is not "usable", and that cost a working loop.** A key was already
set in this environment, belonging to a team with no credits, so every call
returned `403 permission-denied` while the OAuth session beside it worked
perfectly. Preferring the key blindly therefore *broke* the review. A preflight
— a trivial prompt in the image, costing a second — decides which credential is
live before the review commits to one; learning it from a failed review costs
the review and reports a reviewer that did not run.

The fallback copies three files into a temporary directory and mounts each:

| File | Why |
|---|---|
| `auth.json` | The session itself |
| `agent_id` | This machine's agent identity |
| `config.toml` | Settled configuration |

**All three are required, and that was found the only way it could be — by
adding one at a time until the review completed.** With `auth.json` alone the
container takes itself to be a first run, registers a *new* team, and answers
`403: your newly created team doesn't have any credits` while the host keeps
working against the same file. A trivial prompt succeeds where the review does
not, which is what makes the failure so confusing.

They are **copies**, never the originals: the container cannot rewrite this
host's credential file, and a rotated token is discarded with the temp
directory. None of the three is a credential for anything but grok.

## 4. The version is pinned, and not for tidiness

Unpinned, the image takes the latest, and the reviewer drifts from the
developer's client without anyone choosing it. With the host on 0.2.118 and the
image unpinned onto 1.0.0, the newer client resolved the same OAuth session to a
newly created team with no credits — every review dead, host unaffected. A
credential must mean the same thing on both sides of the mount.

`ARG GROK_VERSION` tracks the host. Both are 1.0.0.

## 5. What this does not close

**Egress.** The container reaches the whole network. Confining it to `api.x.ai`
needs an allow-list proxy on an internal network; Docker alone offers "all" or
"none", and "none" stops the review too. The credential half is what the finding
named and what this closes; egress is recorded, not hidden.

**A determined adversary inside the container** still has the repository and the
network. The claim here is narrow and deliberate: the reviewer can no longer
reach the host's other credentials, which is what made the finding sharp.

## 6. Done means

- The full loop runs through `grok-review.sh` and completes — `end_turn`, a real
  verdict, `suggestions.md` written or removed.
- The preflight is exercised both ways: a dead `XAI_API_KEY` falls back with a
  warning, an absent one goes straight to the OAuth copies.
- The fail-closed checks PR-15 added still hold: a cancelled run exits non-zero
  and leaves `suggestions.md` untouched.
- CLAUDE.md's residual paragraph is reconciled in the same change — it currently
  calls the credential half open, and that stops being true here.
