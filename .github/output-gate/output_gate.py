#!/usr/bin/env python3
"""Fail the build when it has written into `src/` or `tests/`.

Section 4.1 states it as a property of the tree: `src/` and `tests/` hold
source, and nothing a build wrote. `Directory.Build.props` is what makes it
true — one `artifacts/` directory at the repository root takes `bin/`, `obj/`
and `publish/` for every project — and this gate is what keeps it true, because
the claim rests on a default the repository does not own.

**The default is wider than its name, and that is the whole reason for a
gate.** `UseArtifactsOutput` moves `MSBuildProjectExtensionsPath` as well as
the intermediate output, so `project.assets.json` and the generated
`.nuget.g.props` land in `artifacts/obj/<Project>/` too and a restore leaves
nothing beside a `.csproj` either. Measured on the SDK `global.json` pins:

    dotnet msbuild <any>.csproj -getProperty:MSBuildProjectExtensionsPath
    -> <repo>/artifacts/obj/<Project>/

Nothing in `Directory.Build.props` asks for that, and nothing in this
repository can hold a future SDK to it.

**Setting `MSBuildProjectExtensionsPath` explicitly was the other option and is
the worse one.** It would restate a path the SDK already derives — a second
place to be wrong about one value — and NuGet resolves it at restore time and
again at build time, failing on an assets file it cannot find if the two
disagree. That buys no coverage at all: the property would be pinned and the
outcome still unmeasured. A gate costs a second on every run where the default
holds, and says so on the day it stops.

**It proves a build ran before it reports that one wrote nothing.** "No `obj/`
under `src/`" is satisfied by a checkout nobody has built, which is the single
state a green result must not cover, so every project must also have its
`artifacts/obj/<Project>/`. That is the positive half of the same claim: the
output is somewhere, and it is there. It is also why this gate runs behind the
build rather than in the fast job with the licence gate — it reads what a build
did, so a restore and a compile are in front of it either way.

**The subject is checked rather than assumed.** A walk that finds no project
satisfies every assertion below, and a gate that quietly stops looking at the
newest tree is this repository's most-repeated failure. So the projects found
on disk under `src/` and `tests/` are compared with the ones `Platform.slnx`
lists, in both directions: a project on disk and not in the solution is one
`dotnet build Platform.slnx` never built, and a project in the solution and not
on disk is a tree this walk is not reading.

    python .github/output-gate/output_gate.py
"""
from __future__ import annotations

import argparse
import os
import sys
import xml.etree.ElementTree as ElementTree
from pathlib import Path

GATE_DIR = Path(__file__).resolve().parent
REPO_ROOT = GATE_DIR.parents[1]

# The two trees Section 4.1 names. `tools/` is deliberately absent: the
# scaffold is stdlib Python that restores nothing, so a directory MSBuild never
# enters cannot fail this gate for the reason the gate exists.
SOURCE_ROOTS = ("src", "tests")

# What MSBuild writes beside a `.csproj` when the redirect is not in force.
# Matched without regard to case, which is `.gitignore`'s spelling of the same
# set — `[Bb]in/` and `[Oo]bj/` — rather than a second opinion about it.
OUTPUT_DIRECTORY_NAMES = frozenset({"bin", "obj"})


def solution_projects(solution: Path) -> dict[str, Path]:
    """Every project `Platform.slnx` lists, keyed by MSBuild project name.

    The key is the file stem because that is what the artefacts layout pivots
    on: `UseArtifactsOutput` puts a project's output under
    `artifacts/obj/$(MSBuildProjectName)/`, and `MSBuildProjectName` is the
    `.csproj` filename without its extension. Keying on anything else would
    make the lookup below a guess.

    Stdlib `ElementTree` rather than `defusedxml`, on the licence gate's
    argument one directory over and for the same reason: no dependency, and the
    input is not untrusted. This parses `Platform.slnx` from the checkout the
    gate is running inside, and a repository that could plant a hostile
    solution file could plant this script instead.
    """
    root = ElementTree.parse(solution).getroot()

    projects: dict[str, Path] = {}
    for element in root.iter("Project"):
        path = element.get("Path")
        if not path:
            continue
        # `.slnx` is written with forward slashes by the SDK and with
        # backslashes by anything that has been through a Windows tool. Both
        # spell the same project, and a gate that reads only one of them
        # reports a whole solution missing from disk.
        relative = Path(path.replace("\\", "/"))
        projects[relative.stem] = relative
    return projects


def walked_projects(repo_root: Path) -> dict[str, Path]:
    """Every `.csproj` on disk under the source roots, keyed the same way."""
    projects: dict[str, Path] = {}
    for source_root in SOURCE_ROOTS:
        for csproj in sorted((repo_root / source_root).rglob("*.csproj")):
            projects[csproj.stem] = csproj.relative_to(repo_root)
    return projects


def compare_subject(walked: dict[str, Path], listed: dict[str, Path]) -> list[str]:
    """The two views of what this gate is looking at, reconciled.

    Both directions are findings, and they are different defects. A project the
    solution lists and the walk cannot find means the walk is reading the wrong
    tree, and every assertion made from it is worth nothing. A project the walk
    finds and the solution does not list is one CI never compiles, so its output
    location has never been exercised by anything.
    """
    findings: list[str] = []

    if not walked and not listed:
        roots = " and ".join(f"{name}/" for name in SOURCE_ROOTS)
        return [
            f"no projects found at all - {roots} hold no .csproj and Platform.slnx "
            f"lists none. Every check below passes over an empty set, which is a "
            f"gate reporting on nothing rather than a tree with nothing wrong"]

    for name in sorted(set(listed) - set(walked)):
        findings.append(
            f"Platform.slnx lists {listed[name].as_posix()}, which is not on disk. "
            f"This walk is reading a tree the solution does not describe, so its "
            f"other findings are about a different repository")

    for name in sorted(set(walked) - set(listed)):
        findings.append(
            f"{walked[name].as_posix()} is on disk and absent from Platform.slnx. "
            f"`dotnet build Platform.slnx` never builds it, so nothing has ever "
            f"measured where its output lands")

    return findings


def find_residue(repo_root: Path) -> list[str]:
    """Any `bin/` or `obj/` under the source roots, outermost first.

    The walk prunes what it reports, so a stale `obj/` full of a previous
    layout's subdirectories is one finding rather than a screenful. The
    outermost directory is also the one to delete, which makes the finding and
    the fix the same sentence.
    """
    findings: list[str] = []
    for source_root in SOURCE_ROOTS:
        for directory, subdirectories, _files in os.walk(repo_root / source_root):
            residue = sorted(
                name for name in subdirectories if name.lower() in OUTPUT_DIRECTORY_NAMES)
            for name in residue:
                relative = (Path(directory) / name).relative_to(repo_root)
                findings.append(
                    f"{relative.as_posix()}/ is build output beside the source it was "
                    f"built from. Delete it: git-ignored output is never removed by a "
                    f"checkout, so a directory written before the redirect landed "
                    f"survives every branch")
            subdirectories[:] = [name for name in subdirectories if name not in residue]
    return findings


def find_missing_output(artifacts: Path, walked: dict[str, Path]) -> list[str]:
    """Every walked project must have an `artifacts/obj/<Project>/`.

    This is the assertion that makes a green result mean something. Without it
    the gate passes on a fresh checkout, on a failed build, and on a build of
    some other solution — three states in which `src/` holds no output for the
    uninteresting reason.
    """
    obj = artifacts / "obj"
    missing = sorted(name for name in walked if not (obj / name).is_dir())
    if not missing:
        return []

    if len(missing) == len(walked):
        return [
            f"artifacts/obj/ has an entry for none of the {len(walked)} project(s): no "
            f"build has run here. Run `dotnet build Platform.slnx` first - this gate "
            f"reads what a build did, and a tree nobody built holds no output anywhere"]

    findings: list[str] = []
    for name in missing:
        findings.append(
            f"artifacts/obj/{name}/ does not exist, though {walked[name].as_posix()} "
            f"does. The build skipped that project, or its output went somewhere this "
            f"gate is not looking")
    return findings


def audit(repo_root: Path) -> list[str]:
    """Every finding, in the order a reader can act on them.

    The subject comes first on purpose: a mismatch there changes what the other
    two checks were even about, so reporting them alongside would be three
    findings where there is one.
    """
    walked = walked_projects(repo_root)
    listed = solution_projects(repo_root / "Platform.slnx")

    findings = compare_subject(walked, listed)
    if findings:
        return findings

    return find_missing_output(repo_root / "artifacts", walked) + find_residue(repo_root)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", type=Path, default=REPO_ROOT)
    args = parser.parse_args(argv)

    repo_root = args.repo.resolve()
    findings = audit(repo_root)

    if findings:
        print(f"Output gate: {len(findings)} finding(s).\n")
        for finding in findings:
            print(f"  {finding}")
        print("\nSection 4.1: src/ and tests/ hold source, and nothing a build wrote. "
              "Directory.Build.props owns where output goes; see its Output comment.")
        return 1

    projects = walked_projects(repo_root)
    roots = " and ".join(f"{name}/" for name in SOURCE_ROOTS)
    # Output stays ASCII. A gate whose job is to report a failure must not be the
    # thing that fails, and stdout encoding on a runner is not ours to assume.
    print(f"Output gate: {len(projects)} project(s) under {roots}. Every one built into "
          f"artifacts/obj/, and neither tree holds a bin/ or obj/ of its own.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
