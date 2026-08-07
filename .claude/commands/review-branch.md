---
description: Review branch vs main for contradictions; recheck suggestions.md when it already exists
argument-hint: "[recheck | full | --local]"
allowed-tools: Read, Grep, Glob, Write, Edit, Bash(git diff:*), Bash(git log:*), Bash(git status:*), Bash(git merge-base:*), Bash(git branch:*), Bash(python .github/licence-gate/licence_gate.py), Bash(dotnet test:*), Bash(dotnet build:*), Bash(rm suggestions.md)
---

Review uncommitted or branch work for **contradictions and self-consistency
issues**, and manage `suggestions.md` at the repository root.

## Mode selection (do this first)

**Before choosing a mode, check whether `suggestions.md` exists at the repo
root.** That file is the running record of open issues; when it is present,
the default job is to re-verify it, not to ignore it and start a fresh review.

| Condition | Mode |
|---|---|
| `suggestions.md` **exists** and `$ARGUMENTS` is empty or `recheck` | **Recheck** (below) |
| `suggestions.md` **missing** and `$ARGUMENTS` is empty | **Full review** — branch vs `main` |
| `$ARGUMENTS` is `full` or `full --local` | **Full review**, even if `suggestions.md` exists (replace the file from scratch after the new pass) |
| `$ARGUMENTS` is `--local` only, no `suggestions.md` | **Full review** of the working tree |
| `$ARGUMENTS` is `--local` and `suggestions.md` exists | **Recheck** first (same as default when the file exists); do not silently switch to a full local sweep unless the user also passed `full` |
| `$ARGUMENTS` is `recheck` and `suggestions.md` **missing** | Stop: say there is nothing to recheck and offer a full review — do not enter recheck mode against a file that is not there |

`recheck` as an explicit argument is kept for clarity; it is **not** required
when the file is already there. Prefer the file’s presence over the empty
argument list.

---

## What counts as a finding

Same bar as `/validate-blueprint`: **two statements that cannot both be true**,
or a statement that cannot be true of the system described — not pure style
taste. Prefer:

1. **Blueprint ↔ code drift** once `src/` exists (samples, pins, type names,
   registration order, endpoints, credentials).
2. **Cross-chapter / CLAUDE.md contradictions** (phase markers, test counts,
   planned vs present trees, counts of classes/packages).
3. **Register drift** — `Directory.Packages.props` vs Appendix B vs §4.4 sample;
   licence gate failing.
4. **Deploy drift** — Compose / Helm / CI vs §14 / §15 claims (ports, secrets,
   service names, healthchecks).
5. **Incomplete reconciliation** — a rule this change states (or a fix it claims)
   that the corpus still violates in the same change set.

Reject as non-findings the house styles `CLAUDE.md` tabulates on purpose
(braceless single statements, file-scoped namespaces, explicit types, British
prose beside real identifier spellings, unpinned Aspire with §4.4 carve-outs,
spread-over-`.ToArray()` when the corpus is already clean).

---

## Recheck mode

**Trigger:** `suggestions.md` is present (default), or `$ARGUMENTS` contains
`recheck`.

1. **Read `suggestions.md` in full.** Enumerate every numbered issue from the
   status table and from the per-issue headings. If the file is empty or has no
   numbered issues, say so and offer a full review rather than inventing items.
2. **For each issue, independently:**
   - Locate the sites named under **Where** / **File** (grep siblings if the
     line numbers moved).
   - Verify whether the stated **Problem** still holds in the current tree
     (and, for branch work, against the current tip — not an old diff alone).
   - Set status to exactly one of:
     - **fixed** — evidence shows the defect is gone
     - **open** — still true
     - **correct** — owner accepted the current state as intentional (only if
       the file already said so, or the user has said so in this conversation)
     - **false positive** — original finding was wrong; only if re-verification
       shows the claim never held / no longer applies as a defect
3. **Do not invent new issues** in recheck mode unless verifying a listed item
   surfaces a **direct regression of that item** (same claim, still broken in a
   new place). A full sweep for new findings is `full`, not recheck.
4. **Rewrite `suggestions.md`:**
   - Update the status table and every per-issue **Status** line.
   - Keep fixed items briefly under their headings (or a “Fixed this pass”
     section) so the next recheck still has context — unless step 5 deletes the
     file.
   - Bump **Re-checked:** to today’s date.
5. **If every issue is `fixed`, `correct`, or `false positive`:**
   - **Delete** `suggestions.md`.
   - Report in chat: all closed, file removed, one-line evidence per former
     issue.
6. **If any issue remains `open`:** keep the file, report counts
   (open / fixed / other).

---

## Full review mode

**Trigger:** no `suggestions.md`, or user passed `full` / `full --local`.

1. **Establish the range.**
   - Branch (default): `MERGE_BASE=$(git merge-base origin/main HEAD)` (fall
     back to `main`), then `git diff --stat` / `--name-only` / full diff
     `"$MERGE_BASE..HEAD"`, plus `git log --oneline "$MERGE_BASE..HEAD"`.
   - `--local`: `git status --short` and `git diff HEAD` (include untracked
     that matter; skip bulk tooling noise).
2. **Read the change.** Prefer full source of load-bearing files over the
   diff alone. Grep the rest of `docs/backend-architecture/`, `CLAUDE.md`,
   and `src/` / `tests/` / `deploy/` for every claim the change touches.
3. **Run cheap gates when the range touches them.**
   - Packages / Appendix B: `python .github/licence-gate/licence_gate.py`
   - Tests / counts CLAUDE asserts: `dotnet test Platform.slnx` when useful
4. **Author findings only when verified.** Quote the conflicting sites.
   Severity: **bug** | **suggestion** | **nit**.
5. **Write `suggestions.md` at the repo root** when any issue is open. Shape:

   ```markdown
   # Suggestions — branch vs `main`   # or "local uncommitted"

   **Reviewed:** <ISO date>
   **Branch:** <name>
   **Base:** origin/main @ <short sha>
   **Scope:** <one line>
   **Diff:** <N files, +/- lines>

   ## Overall
   <2–4 sentences>

   | # | Severity | Item | Status |
   |---|---|---|---|
   | **1** | bug | … | open |

   ## Bugs / Suggestions / Nits
   ### 1. …

   | | |
   |---|---|
   | **Where** | … |
   | **Status** | open |

   **Problem.** …
   **Suggestion.** …

   ## What looks good (no action)
   …

   ## Recommended fix order
   …
   ```

6. **If the review finds nothing open**, do **not** create `suggestions.md`.
   If `full` was requested and an old file existed, **delete** it when the new
   pass is clean; **replace** it when the new pass has findings.
7. **`suggestions.md` is working state.** Never commit it. Do not add it to
   the index. Say so if the user is about to ship.

---

## Report (chat)

Always end with:

- Mode actually run: **recheck** or **full** (and branch/base or local)
- Whether `suggestions.md` was present at start
- Issue counts by status (open / fixed / correct / false positive)
- Path to `suggestions.md` if kept, or that it was deleted / not created
- One-line top remaining findings (if any)

Do not fix the findings in this command unless the user explicitly asks to
apply them after the review.
