---
description: Verify links, section cross-references and nav footers across the blueprint
allowed-tools: Read, Grep, Glob, Edit, Bash(ls:*), Bash(wc:*), Bash(comm:*)
---

Check navigational integrity across `docs/backend-architecture/`. Structural
only — self-consistency of content is `/validate-blueprint`.

## Checks

1. **Relative links resolve.** Every `](*.md)` and `](*.md#anchor)` target
   exists. Every `#anchor` matches a real heading slug in the target file.
2. **Section references resolve.** Every `§n` and `§n.m` corresponds to a
   heading that exists. Flag references to sections that were renumbered away.
3. **Link text agrees with target.** `[§9.3](09-messaging.md)` must point at
   chapter 9. A link whose text names one section and whose href names another
   is a defect even when both exist.
4. **Nav footers.** Every chapter and appendix ends with exactly one `---`, one
   blank line, then a single footer line using ` · ` separators. Previous and
   next targets must match the reading order in
   `docs/backend-architecture/README.md`. First chapter has no `←`; last
   appendix has no `→`. Flag doubled rules — that regression has appeared
   before.
5. **Index completeness.** Every `.md` in the directory (except `README.md`)
   appears exactly once in the README chapter table, and every table row points
   at a file that exists.
6. **Root README.** `../../README.md`'s entry point still resolves.
7. **Orphans.** Any file no other file links to.

## Report

Group findings by check, cite `file:line`, and state the fix. Apply the
unambiguous fixes (broken relative path where the intended target is obvious,
missing/doubled rule, footer neighbour out of order). Ask before changing
anything where the intended target is genuinely uncertain.

Finish with a one-line count per check, including the ones that passed.
