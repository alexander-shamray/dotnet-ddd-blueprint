---
description: Multi-pass self-consistency audit of the blueprint, and of the code against it
argument-hint: "[chapter file or topic to focus on — omit for a full sweep]"
allowed-tools: Read, Grep, Glob, Edit, Bash(git diff:*), Bash(git log:*), Bash(wc:*), Bash(ls:*)
---

Audit `docs/backend-architecture/` for internal contradictions — and, once the
solution exists, for disagreement between the blueprint and the code.

Scope: $1 — if empty, sweep the whole blueprint; if a filename, audit that
chapter against every other chapter; if a topic, trace that topic everywhere it
appears.

**First, establish the phase.** If `Platform.slnx` (or any `src/`) exists, the
code is in scope and check 9 below applies. If not, this is a docs-only audit
and check 9 is skipped — say which you ran.

## What counts as a finding

A finding is **two statements in the blueprint that cannot both be true**, or a
statement that cannot be true of the system the blueprint describes. Not style,
not tone, not "this could be clearer". Specifically hunt for:

1. **Numeric drift** — a timeout, retry count, TTL, page-size clamp, batch size
   or SLO stated as one value in a chapter and another in an appendix or a code
   sample. Reconcile to the value the surrounding argument requires, not to
   whichever appeared first.
2. **Type and member drift** — a type, interface, method or property named one
   way in a sample and another in prose, Appendix D, or a registration list.
3. **Contract drift** — a route, event name, queue name, header, claim, health
   endpoint or configuration key spelled differently across chapters.
4. **Ordering and lifecycle claims** — pipeline behaviour order, middleware
   order, DI registration order, dispatch timing (before/after `SaveChanges`),
   transaction boundaries. These contradict quietly and often.
5. **Rule violations** — a sample that breaks a rule the blueprint states
   elsewhere (e.g. a Domain or Application type touching an infrastructure
   library the ADRs forbid; a route missing a policy the gateway chapter
   mandates; an `/api` prefix rule broken).
6. **Mis-citations** — `§9.4` pointing at a section that does not state the
   claim, a link whose text and target disagree, a reference to a section or
   file that does not exist.
7. **Register drift** — a package used in a sample but absent from
   `appendix-b-licences.md`; an ADR referenced by number that does not exist;
   a type the blueprint *defines* that is absent from
   `appendix-d-type-inventory.md`. Framework and library types are out of scope
   for D — D.6 assumes them wholesale — so do not report `IServiceScope` or
   `HttpResponseMessage` as missing rows. If a sample names a type from a
   library D.6 does not list, the fix is a word in D.6 and a row in Appendix B,
   not a row in D.1–D.5.
8. **Terminology drift** — the same concept under two names ("smoke run" vs
   "smoke test"), or one name for two concepts.
9. **Code ↔ blueprint drift** (only when `src/` exists) — the same eight classes
   above, checked across the boundary rather than within the docs:
   - A type, interface or method in `src/` whose name, signature or namespace
     differs from the sample or from `appendix-d-type-inventory.md`.
   - A number in code — timeout, retry count, TTL, page-size clamp, batch size —
     that differs from the chapter that specifies it.
   - A route, event name, queue name, header, claim or config key that differs
     between an endpoint or consumer and the chapter defining it.
   - A package in `Directory.Packages.props` that is missing from
     `appendix-b-licences.md`, or pinned to a different version than the register
     or a chapter states.
   - A DI registration in `AddXApplication()` / `AddXInfrastructure()` whose set
     or order contradicts the registration list in the chapter or Appendix D.
   - A design rule the ADRs state that the code breaks — Application or Domain
     touching MassTransit or EF Core, a second composition root, a shared table
     across services.

   **The code is not automatically right.** Decide which side the rest of the
   system depends on, exactly as for two chapters. If the blueprint is the
   better answer, the finding is against the code — report it and say so rather
   than quietly amending the spec to match what was built.

## Method

Work in passes. Each pass picks one axis from the list above and traces it
across all 20 files — do not read chapter-by-chapter, read claim-by-claim.

For each candidate:

- Grep for **every** occurrence of the identifier, number or phrase before
  deciding which side is wrong. Report the count and the sites.
- Decide which statement the rest of the blueprint depends on. That one wins;
  amend the others.
- Apply the fix to **all** sites in one edit pass. A half-applied
  reconciliation is worse than none.
- If both statements are defensible and the conflict is genuine design
  ambiguity, do not silently pick one — surface it and ask.

Keep going until a full pass produces no findings.

## Report

For each finding, one block:

```
FINDING — <one-line claim of the defect>
  Sites:    07-persistence.md:112, 09-messaging.md:340, appendix-d:44
  Conflict: <A says X; B says Y>
  Resolved: <what you changed and why that side won>
```

For a code ↔ blueprint finding, add a `Verdict:` line naming which side is
wrong (`code` or `blueprint`) before `Resolved:`. Never edit `src/` as part of
this audit — report the code-side findings and let the fix be its own change
with its own tests.

End with the pass count and a plain statement of whether the last pass was
clean. If you did not reach a clean pass, say so — do not round up.

## Do not

- Do not rewrite prose you merely dislike.
- Do not "fix" the settled choices `CLAUDE.md` tabulates — file-scoped
  namespaces, expression-bodied members and braceless single statements are
  deliberate in both docs and source. Note that `var` is **not** among them:
  explicit types are the rule, with only the four exceptions CLAUDE.md lists.
- Do not renumber ADRs or chapters.
- Do not edit anything under `src/` or `tests/`.
- Do not touch `.remember/`.
