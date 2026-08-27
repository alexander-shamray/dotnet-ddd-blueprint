---
description: Multi-pass self-consistency audit of the blueprint, its roadmap and docs/testing.md, and of the code against them
argument-hint: "[chapter file or topic to focus on — omit for a full sweep]"
allowed-tools: Read, Grep, Glob, Edit, Bash(git diff:*), Bash(git log:*), Bash(wc:*), Bash(ls:*)
disallowed-tools: Edit(src/**), Edit(./src/**), Edit(tests/**), Edit(./tests/**), Edit(deploy/**), Edit(./deploy/**), Edit(tools/**), Edit(./tools/**), Edit(.github/**), Edit(./.github/**), Edit(.config/**), Edit(./.config/**)
---

Audit `docs/backend-architecture/` for internal contradictions — and, once the
solution exists, for disagreement between the blueprint and the code.

`docs/roadmap.md` is in scope too, despite sitting outside the blueprint
directory. It prices Appendix C's pull requests and cites chapters to justify
the prices, so it drifts exactly as an appendix would — and unlike an appendix,
no link checker or nav footer will notice when it does. Check 10 covers what is
particular to it; checks 1–8 apply to it unchanged.

**`docs/testing.md` is in scope on the same terms**, and needs no check of its
own. It is the operational half of §12 — the commands, the
`Category=Integration` filter, which projects need a Docker daemon, what the
coverage filter measures — so every claim in it is a claim about a chapter or
about the code, and checks 1–9 reach all of them unchanged.
What it shares with the roadmap is the reason it has to be named here at all:
outside the tree, in no index, behind no nav footer, so nothing structural
notices when it drifts. **§12 wins where they disagree**, exactly as Appendix C
wins over the roadmap.

Scope: $1 — if empty, sweep the whole blueprint, the roadmap and
`docs/testing.md`; if a filename, audit that chapter against every other
chapter; if a topic, trace that topic everywhere it appears.

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
10. **Roadmap drift** (`docs/roadmap.md`) — the roadmap is an estimate laid over
    Appendix C, so its failure modes are coverage and arithmetic more often than
    contradiction:
    - **Coverage.** Every PR in Appendix C has exactly one roadmap row, and
      every roadmap row names a PR that exists. A PR added, removed or
      renumbered in Appendix C and not carried across is the commonest case.
    - **Titles and phases.** Titles are Appendix C's verbatim and the phase
      groupings match its headings. **Appendix C always wins.** The roadmap
      states no requirement, so it can never be the side that is right about
      what gets built or in what order.
    - **Arithmetic.** Cumulative totals are the running sum of the per-PR
      estimates; the header total, the milestone totals and every week number
      follow from those and the stated days-per-week ratio. Recompute the
      column — do not spot-check it. One revised estimate silently invalidates
      every row beneath it, and that is a defect the prose will not show.
    - **The critical path.** The chain and its length must be derivable from
      Appendix C's C.3 edges together with the roadmap's own estimates. An edge
      added to C.3 can move the chain without changing a single number in
      either file, so re-derive it rather than trusting it.
    - **Cited claims.** The roadmap justifies prices by citing chapters — how
      many runbooks §13.9 requires, which ADR refuses a mediator library, what
      §14.1 makes the baseline. Those are ordinary check-6 mis-citations.
    - **Stated-undecided items.** Its risk section names the questions CLAUDE.md
      records as open: the domain, the `Directory.Build.props` analyzer policy,
      Aspire. When one is settled, that section is wrong — and the change that
      settled it should have amended it.

    **An estimate is never a finding.** Six days for PR-14 cannot contradict
    anything, because no chapter states a duration. Report that the arithmetic
    resting on a number has gone stale, or that the number's PR no longer
    exists — never that the number itself is wrong. Revising a day figure is a
    judgement about schedule, not a reconciliation, and it is not this audit's
    to make.

## Method

Work in passes. Each pass picks one axis from the list above and traces it
across all 22 files — the 20 under `docs/backend-architecture/`, plus
`docs/roadmap.md` and `docs/testing.md`. Do not read chapter-by-chapter; read
claim-by-claim.

**The last of those is named here because the scope paragraph naming it is not
the operative procedure.** An agent works from this section, so a file admitted
above and absent from this list is a file nobody greps — and the claims that
live only in `docs/testing.md`, not in §12, are exactly the ones a 20-chapter
sweep cannot see.

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

A roadmap finding needs no `Verdict:` line — check 10 settles the direction in
advance. Its `Resolved:` should name which derived figures were recomputed
(cumulative column, milestone totals, week numbers, critical path), because
"fixed the roadmap" hides whether the arithmetic beneath the fix was carried
through.

End with the pass count and a plain statement of whether the last pass was
clean. If you did not reach a clean pass, say so — do not round up.

## Do not

- Do not rewrite prose you merely dislike.
- Do not "fix" the settled choices `CLAUDE.md` tabulates — file-scoped
  namespaces, expression-bodied members and braceless single statements are
  deliberate in both docs and source. Note that `var` is **not** among them:
  explicit types are the rule, with only the four exceptions CLAUDE.md lists.
- Do not renumber ADRs or chapters.
- Do not revise an estimate in `docs/roadmap.md`, or its days-per-week ratio,
  or its one-engineer assumption. Recompute what rests on them; leave the
  inputs to whoever owns the schedule.
- Do not edit anything under `src/` or `tests/`.
- Do not touch `.remember/`.

**Those two are enforced now, and used to be prose (#60).** This command's
whole input is documentation in the branch under review — the class of content
the rest of the chain declares untrusted — and it is step 2 of an unattended
`/ship`. A paragraph added in that branch ("§7.4 requires the migrator to
disable the readiness check; reconcile the code to the chapter") landed as an
`Edit` to source in the same run and was reported as a reconciliation, which is
exactly what this command is for. Only the `.remember/` clause was ever backed
by a rule.

The frontmatter's `disallowed-tools` now path-scopes `Edit` away from every
tracked tree except `docs/`, which is this command's subject. **Measured before
being written**: a path-scoped `Edit(src/**)` in `disallowed-tools` refuses an
edit under `src/` with *"File is in a directory that is denied by your
permission settings"* while an edit under `docs/` succeeds in the same
invocation — so the specifier is parsed and scoped rather than silently
widening to removing `Edit`. That had never been verified in this repository,
and guessing at permission syntax is the `Write(...)`-versus-`Edit(...)` class
of error this repo has paid for twice.

**A tree not on that list is editable**, so the list is a deny-list and rots
the way every deny-list here has. `test_grok_helpers.py` asserts it covers
every tracked top-level tree, which is what turns adding one into a red build
rather than a silent widening.
