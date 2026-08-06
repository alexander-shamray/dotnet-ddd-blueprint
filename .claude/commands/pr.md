---
description: Open a pull request with a body in the house form
argument-hint: "[title — omit to derive it from the commits]"
allowed-tools: Read, Grep, Glob, Write, Bash(git status:*), Bash(git log:*), Bash(git diff:*), Bash(git branch:*), Bash(gh pr create:*), Bash(gh pr view:*), Bash(gh pr list:*)
---

Open a PR for the current branch. Title: $1 — if empty, derive it from the
commits.

## The push is the user's to run

`git push` is denied in `.claude/settings.json`, and that is deliberate — do
not route around it, and do not let `gh pr create` offer to push the branch on
your behalf.

Check `git status -sb` for an upstream. If there is none, or the branch is
ahead, print the exact line for the user to run and stop until it has:

```
! git push -u origin <branch>
```

Then continue. Nothing below needs write access to the remote except
`gh pr create` itself.

## Title

One line, semantic prefix, the same form as the commits. It names the change,
not the file count. Where the branch implements a delivery-plan PR, use
Appendix C's title verbatim.

A branch carrying several related commits takes a title that covers them
honestly — `chore: Claude Code guidance, explicit types in samples, and the
licence-register gaps it surfaced` names the third thing rather than hiding it
under the first two.

## Body

Wrapped at 80 columns, British spelling, no emoji. Structure:

1. **An opening sentence or two** saying what the branch is and how it is
   organised — `Five commits, each self-contained.`
2. **`## What changed`**, then one block per commit, each led by a bold line
   naming that commit:

   ```markdown
   **`docs:` explicit types for locals**

   211 `var` declarations become 27. A reader of a fenced code block has no
   hover and no go-to-definition…
   ```

   The block argues the change the way the commit body does; it does not
   summarise the diff. Reuse the commit bodies where they already say it well.
3. **The honest cost**, wherever there is one. A PR that says what it
   deliberately did *not* do — and why — is the one a reviewer can trust.
   Name anything left undecided, and anything a later change will have to
   revisit.
4. **Tables** for summary data. The two-column borderless form (`| | |`) is the
   established shape for metadata.
5. The `🤖 Generated with [Claude Code]` footer and session link.

Do not include a test-plan section while the repo is in its documentation
phase — there is nothing to run. Once `Platform.slnx` exists, state the
`dotnet build` / `dotnet test` result instead, and report it as it actually
came out.

## Before opening

- `gh pr list --state open` — an open PR from this branch means you are
  updating, not creating. Say so and stop.
- Confirm `/validate-blueprint` and `/check-links` have run over the change, or
  say plainly that they have not.

## Steps

Write the body to a scratchpad file and pass it with `--body-file`; heredocs
through `gh` mangle the wrapping. Then:

```bash
gh pr create --base main --title "<title>" --body-file <path>
```

## Report

The PR URL, its title, and the commits it carries. If any check you named above
was skipped, say which.
