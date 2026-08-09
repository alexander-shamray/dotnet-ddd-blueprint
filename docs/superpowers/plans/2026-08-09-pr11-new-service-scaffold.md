# PR-11 — the new-service scaffold — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Land `tools/new-service/new_service.py`, a stdlib-Python script that
renders a new service from the Catalog template — five projects, four test
projects, the Compose pair, the solution entries, the override, the env vars
and the ports row — and prove it by scaffolding Ordering, building it, running
its tests and discarding it.

**Architecture:** There is no template directory. The script reads
`src/Services/Catalog` and `tests/Catalog.*` at run time, excludes the slice by
explicit path, applies seven anchored patches to the wiring that names the
slice, renders everything in memory, validates, and only then writes. Five
shared files are edited by extracting and renaming their Catalog block.

**Tech Stack:** Python 3.12 stdlib (`argparse`, `pathlib`, `unittest`) — no
dependencies, no restore, mirroring the licence gate.

## Global Constraints

- The spec:
  `docs/superpowers/specs/2026-08-09-pr11-new-service-scaffold-design.md`.
  Where it and the blueprint disagree, the blueprint wins.
- The script writes **nothing** unless every anchor matched exactly once and
  every straggler check passed. Partial output is a defect, not a mode.
- No new NuGet or Python package — so no `Directory.Packages.props` change and
  no Appendix B row.
- The generated C# is Catalog's C# renamed: house style is inherited, not
  reimplemented. The only hand-authored generated file is `AssemblyMarker.cs`,
  which obeys the same rules — file-scoped namespace, blank line after it,
  CRLF, 120 columns.
- Line endings: the repository is `*.cs text eol=crlf`. The script reads and
  writes with `newline=''` so it neither introduces nor strips CRLF.
- Prose wraps at 80 columns, British spelling, `§n` cross-references.
- Commits are semantic and present-tense; each carries an arguing body.

---

### Task 1: The renderer, test-first

- [ ] `test_new_service.py` first: render `Ordering` from the real repository
      into a temp dir, assert the nine projects and the marker exist.
- [ ] `new_service.py`: `ScaffoldError`, `Names` (Pascal/lower/upper),
      `plan(repo_root, name, port, migration_id) -> Plan`, `apply(plan)`,
      `main(argv)`.
- [ ] Copy walk over the five source roots and four test roots, skipping
      `bin/` and `obj/`, excluding the slice paths of the spec's §2.
- [ ] Rename in path and content: `Catalog`→`Ordering`, `catalog`→`ordering`,
      `CATALOG`→`ORDERING`.
- [ ] Straggler check over rendered text and paths for all five tokens.

### Task 2: The seven anchored patches

- [ ] One `PATCHES` table: `(source relative path, old, new)`, matched against
      Catalog text **before** renaming, each asserted to occur exactly once.
- [ ] The two DI files, `Program.cs`, `CatalogDbContext.cs`, the two
      `ArchitectureTests.cs`, `DependencyInjectionTests.cs` and
      `DatabaseSmokeTests.cs`.
- [ ] `AssemblyMarker.cs` emitted into `{Service}.Domain/`, with the doc
      comment that tells the reader to delete it.
- [ ] A test that deleting an anchor from a template copy raises
      `ScaffoldError` naming the file.

### Task 3: The migration and its snapshot

- [ ] Fresh UTC migration id, `--migration-id` for determinism in tests.
- [ ] `InitialCreate.cs` and `.Designer.cs` copied and renamed; the
      `[Migration("…")]` id and the file name must agree.
- [ ] The model snapshot derived from the copied `.Designer.cs` by four
      anchored substitutions; Catalog's own snapshot is never read.

### Task 4: The five shared files

- [ ] `Platform.slnx` — a `/src/Services/{Name}/` folder with five projects in
      alphabetical position, four test entries likewise.
- [ ] `docker-compose.yml` — Catalog's pair extracted, renamed, port
      substituted, inserted before `otel-collector`.
- [ ] `docker-compose.infra-only.yml` — two profile entries.
- [ ] `.env.example` — Catalog's commented pair, extracted and renamed.
- [ ] `deploy/compose/README.md` — one application-services row.
- [ ] Refusals: bad name, `Catalog`, existing directory, taken port.

### Task 5: CLI, CI and the tool's own README

- [ ] `main` — `argparse`, required `--port`, exit 1 with the error on stderr,
      a one-line summary of what was written on success.
- [ ] `tools/new-service/README.md` — the command, the arguments, what it
      writes, and the manual steps it does not do.
- [ ] `.github/workflows/ci.yml` — a `scaffold` job running `python -m
      unittest`, beside `licence-gate` and gating nothing.

### Task 6: The dogfood run

- [ ] Scaffold `Ordering --port 5101` into the working tree.
- [ ] `dotnet restore` / `build Platform.slnx` clean, zero warnings.
- [ ] `dotnet test Platform.slnx` green, Ordering's own suites included.
- [ ] `dotnet ef migrations add Probe` against Ordering — the generated `Up`
      must be empty, proving the snapshot.
- [ ] Revert every generated and edited file. **Ordering is PR-18's**, not
      this one's.

### Task 7: Docs, validate, ship

- [ ] §4.1 gains `tools/` in its tree; a new **§4.5 Adding a service**
      documents the script and what it leaves to the author.
- [ ] `docs/roadmap.md`: what PR-18 inherits and what it still writes, and the
      scaffold's domain-neutrality against the domain risk.
- [ ] CLAUDE.md phase note: PR-11 landed, PR-12 next, `tools/` in the tree,
      the findings that bind later services.
- [ ] `/validate-blueprint`, then `/ship`.
