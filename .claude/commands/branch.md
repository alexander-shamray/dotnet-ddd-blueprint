---
description: Start a correctly named working branch, carrying any uncommitted work with it
argument-hint: "[what the change does] — omit to derive it from the uncommitted work"
allowed-tools: Read, Grep, Bash(git status:*), Bash(git diff:*), Bash(git branch:*), Bash(git log:*), Bash(git fetch:*), Bash(git checkout -b:*), Bash(git switch -c:*), Bash(git rev-parse:*)
---

Create a branch for: $ARGUMENTS — if empty, derive it from the uncommitted
work.

`main` is not a working branch. If work has already started on it, this command
is how it gets moved off — uncommitted changes follow a `checkout -b`, so
nothing is lost and nothing needs stashing.

## Steps

1. **Read the current state.** `git branch --show-current` and
   `git status --short`. Three cases:
   - **On `main`, clean** — fetch, then branch from `origin/main`.
   - **On `main`, dirty** — branch from `HEAD` and carry the changes across.
     Say that you did. Do **not** stash, reset or clean: `git reset --hard` and
     `git clean` are denied in `.claude/settings.json` and stashing hides work
     the user can see right now.
   - **Already on a branch** — stop and say so. Report the branch, its
     upstream and whether the tree is dirty, then ask whether this is a second
     change wanting its own branch or a continuation of the current one. Do not
     branch off a feature branch on your own initiative.
2. **Derive the description if none was given.** Read `git status --short` and
   then the diff itself — `git diff` and `git diff --stat`, plus the untracked
   files. The stat names files; the branch has to name the *change*, and only
   the diff says what it is.

   The type falls out of what was touched, and maps onto the same table below:

   | | |
   |---|---|
   | `docs/**` only | `docs/` |
   | `.editorconfig`, `CLAUDE.md`, `.claude/**`, CI, `Directory.*.props` | `chore/` |
   | `src/**` or `tests/**` | `feat(<scope>)/`, `fix/` or `refactor/` — the diff decides which |

   **A mixed tree takes the type of the change that carries the argument, not
   the one with the most files.** `chore/repo-guidance-and-explicit-types`
   touched `CLAUDE.md`, `.editorconfig` and nine chapters, and is `chore/`
   because the guidance change was the point and the chapter edits followed
   from it.

   Two genuinely unrelated changes cannot both be in the name. Say so, name the
   dominant one, and flag that the other may want its own branch — `/commit`
   will split them into separate commits either way, but the branch can only
   describe one of them.

   A clean tree and no argument leaves nothing to derive from. Stop and ask
   what the branch is for rather than inventing a name.
3. **Name it `<type>/<kebab-summary>`.** The type is the one the commit will
   carry, so the branch and its commits agree:

   | | |
   |---|---|
   | `docs/` | Blueprint prose, structure, cross-references |
   | `chore/` | Repo config — `.editorconfig`, `CLAUDE.md`, commands, CI |
   | `feat(<scope>)/` | Solution code that adds behaviour |
   | `fix/` | A defect in either |
   | `refactor/` | Shape change, no behaviour change |

   Established names to match: `docs/split-by-chapter`,
   `chore/repo-guidance-and-explicit-types`. Summarise the change, do not
   describe the files — `docs/sql-sample-indentation`, not `docs/update-docs`.

   **Implementing a delivery-plan PR?** Grep `appendix-c-delivery-plan.md` for
   the PR and derive the name from its title, so the branch, the commit and the
   plan all read the same. PR-01's title is
   `chore: solution structure, SDK pin, central package management, CI skeleton`
   → `chore/solution-structure`.
4. **Check the name is free** — `git branch --list` and `git branch -a`. A
   name that already exists locally or on the remote means the work may already
   be underway; say so rather than picking a variant.
5. **Create it** with `git checkout -b <name>`. No upstream is set here —
   `/pr` does that on the first push.

## Report

The branch created, what it was cut from, and — if the tree was dirty — the
files carried across, so the user can confirm they belong on this branch and
not the next one.

**A derived name is a guess, so show your work.** State that no description was
passed, give the one-line reading of the diff the name came from, and say the
name is still free to change: no upstream exists until `/pr` pushes, so
`git branch -m <better-name>` costs nothing until then.
