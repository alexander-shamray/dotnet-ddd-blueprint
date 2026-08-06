---
description: Append an architecture decision record to Appendix A in the established form
argument-hint: "<short title>  e.g. Cursor pagination for list endpoints"
allowed-tools: Read, Grep, Glob, Edit
---

Add an ADR titled `$ARGUMENTS` to
`docs/backend-architecture/appendix-a-adrs.md`.

## Steps

1. **Find the next number.** Grep `^## ADR-` and take the highest + 1,
   zero-padded to three digits. **Never reuse or renumber.** If this decision
   reverses an existing ADR, write a new one and add a `**Supersedes.**` line;
   amend the old one with `**Superseded by ADR-NNN.**` rather than deleting it.
2. **Append** at the end of the ADR list, before any trailing rule and nav
   footer, in exactly this form:

   ```markdown
   ## ADR-0NN — <Title>

   **Decision.** <What was decided. Imperative, specific, testable.>
   **Why.** <The forces. What breaks without it.>
   **Consequences.** <What this costs. The honest downside, the thing a future
   reader will resent. This is the part that earns the ADR its keep.>
   ```

   No blank lines between the three bold-led sentences. Prose wrapped at 80
   columns.
3. **Cross-reference.** Link the chapter section the ADR governs —
   `([§9.3](09-messaging.md))` — and check whether that chapter should link back.
4. **Check for conflict.** Grep the existing ADRs for the same subject. An ADR
   that contradicts an earlier one without superseding it is the exact defect
   `/validate-blueprint` exists to catch.
5. **Register any new dependency** the decision introduces in
   `appendix-b-licences.md` with its licence and role. Versions belong in
   `Directory.Packages.props`; state one in the register only where the version
   *is* the decision, as with MassTransit 8.x.

## Report

The number assigned, the sections linked, and anything you found that the new
ADR touches but does not yet reconcile.
