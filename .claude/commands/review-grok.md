---
description: Triage an external review of the blueprint into a resolution record
argument-hint: "[path to the review, or paste it after the command]"
allowed-tools: Read, Grep, Glob, Edit, Write, Bash(git diff:*), Bash(git log:*), Bash(wc:*), Bash(ls:*)
---

Work through the review in $ARGUMENTS — a file path, or the text pasted after
the command.

Unlike `/review-copilot`, this review arrives as prose from outside the repo
with no line anchors, so the first job is to locate what each finding is
actually about before deciding whether it is true.

## Method

1. **Enumerate first, fix nothing.** Split the review into discrete findings and
   number them. A paragraph making three claims is three findings. A finding
   that only says "consider X" with no defect behind it is not a finding — say
   so and drop it.
2. **Locate each one.** Grep for the identifier, number or phrase it names.
   Record every site, not the first. A finding you cannot locate is reported as
   unlocatable, not quietly assumed true.
3. **Adjudicate against the blueprint, not the reviewer.** The bar is
   `/validate-blueprint`'s: two statements that cannot both be true, or a
   statement that cannot be true of the system described. An external reviewer
   does not know this repo's settled choices, and the ones it most often
   flags are the ones `CLAUDE.md` tabulates deliberately — braceless single
   statements, file-scoped namespaces, explicit types, British prose beside
   real identifier spellings, the unpinned Aspire rows. Reject those by name.
4. **Watch for the good class of finding.** External reviews have been right
   here about exactly one thing repeatedly: **register and version drift** —
   a package used in a sample but missing from `appendix-b-licences.md`, a
   version stated in two places, a licence claim that does not match the
   package. Check those properly before dismissing them.
5. **Fix every site in one pass**, then re-grep to confirm none survived.

## The resolution record

Findings arrive faster than they can be applied, and the middle state is where
they get lost. Write a record to the scratchpad — not the repo, it is working
state — with one row per finding:

```markdown
| # | Finding | Status |
|---|---------|--------|
| 1 | MassTransit pinned at 9.x in §4.4, 8.x in Appendix B | Fixed |
```

Then a block per fixed finding:

```markdown
### 1. <one-line claim>

**Was.** <the text or value as it stood, quoted>
**Resolution.** <what changed, at which sites, and why that side won>
```

Statuses are `Fixed`, `Rejected — <rule>`, `Rejected — not true`,
`Unlocatable`, `Needs a decision`. A second table holds anything **found while
fixing** — the defects the review did not name but the grep turned up. That
table has historically been the more valuable of the two; do not fold it into
the first.

## Report

Counts by status, the record's path, and every `Needs a decision` row spelled
out in full — those are the only ones that stop here rather than in a commit.
