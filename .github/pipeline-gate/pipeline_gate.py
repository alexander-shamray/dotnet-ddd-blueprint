#!/usr/bin/env python3
"""PR-25's quality gates: the pipeline asserting things about itself.

Three subcommands, and they answer the three ways §15.1's staged pipeline can
be quietly wrong: a deployable nothing filters, an image nothing builds, and a
stage nothing ran.

`filters` is §15.1's own instruction -- "assert that every immediate child of
`src/`, and every immediate child of `src/Services/`, appears in at least one
filter -- and fail on the one that does not". A path filter is a deployable
inventory, and an inventory drifts: a missing top-level entry is what left
`src/Gateway/**` and `src/BFF/**` unbuilt, and a missing entry under
`Services/` is quieter still, because the parent is spoken for by its siblings
and the list looks complete right up until one service stops being deployed.

`images` is the same inventory one artefact over, and §15.1's per-service image
build is what makes it one: every Dockerfile under `src/` must be built by some
matrix entry, and every entry must name a Dockerfile that exists. Its third
check is the one neither direction can see — a matrix entry reading
`needs.changes.outputs[matrix.filter]` on a name no filter defines evaluates to
the empty string rather than erroring, so the step is skipped and the job
reports success having built nothing.

`stages` is the one docs/testing.md names: "whoever writes the staged pipeline
should assert a floor on each stage's count rather than trusting a green exit."
The trap it closes is §12.1's oldest one wearing different clothes -- a missing
test adapter makes `dotnet test` report no tests and exit **zero**, and a
mistyped `--filter` does exactly the same. Splitting one run into three is
three new ways to select nothing and be congratulated for it.

**A floor is the weaker half of that check and it is here because the chapter
asks for it.** A number in a file drifts, and this repository has a section
about numbers that drift; what carries the weight instead is the structural
half, which has no number in it at all: every test project in `Platform.slnx`
must appear in some stage, no stage may be empty, and no test may run in two.
Those hold whatever the counts become, and they fail on exactly the defect a
floor is groping for.

Stdlib only, on the licence gate's terms. `filters` needs nothing but the
checkout and runs before any build; `stages` reads what the test steps wrote.

    py -3.12 .github/pipeline-gate/pipeline_gate.py filters
    py -3.12 .github/pipeline-gate/pipeline_gate.py stages TestResults/architecture TestResults/unit TestResults/integration
"""

from __future__ import annotations

import argparse
import re
import sys
import xml.etree.ElementTree as ElementTree
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
WORKFLOW = ROOT / ".github" / "workflows" / "ci.yml"
SOLUTION = ROOT / "Platform.slnx"

# The floors, with the run that produced them.
#
# MEASURED, not guessed: `dotnet test Platform.slnx --no-build -c Release` under
# each stage's filter on this repository, summed over the thirteen per-project
# totals -- 18 + 606 + 171 = 795, which is the figure CLAUDE.md and
# docs/testing.md carry and the sum docs/testing.md points at ("624 and 171
# summing to 795", 624 being the architecture and unit stages together).
#
# THE NUMBERS BELOW ARE NOT THOSE COUNTS, and the gap is deliberate. A floor
# set at the measurement is a ratchet: it fails on the day somebody legitimately
# deletes a test class, which is a green change made red by bookkeeping. What
# this is groping for is an order-of-magnitude miss -- a filter that selected
# nothing, or a tenth of what it should -- and a round number well under the
# measurement catches that without firing on ordinary churn. The structural
# checks below are what catch the small stuff, and they have no number to age.
STAGE_FLOORS = {
    "architecture": 15,     # measured 18
    "unit": 550,            # measured 606
    "integration": 150,     # measured 171
}


def fail(problems: list[str], subject: str) -> int:
    if not problems:
        return 0
    print(f"pipeline-gate: {len(problems)} problem(s) with {subject}:\n", file=sys.stderr)
    for problem in problems:
        print(f"  - {problem}", file=sys.stderr)
    return 1


# --------------------------------------------------------------------------
# filters
# --------------------------------------------------------------------------

def read_filters(workflow_text: str) -> list[str]:
    """Every path pattern inside the paths-filter step's `filters:` block.

    Text rather than YAML, for the reason `deploy/observability/check.py` gives
    one tree over: there is no stdlib YAML parser, and a gate that needs a
    `pip install` is a gate that gets skipped. The block is a YAML string
    inside a YAML document either way, so a parser would hand back the same
    text to be parsed a second time.

    The anchor is `filters: |` and the block ends at the first line that is
    neither blank, a comment, nor indented past it.
    """
    lines = workflow_text.splitlines()
    for index, line in enumerate(lines):
        match = re.match(r"^(\s*)filters:\s*\|", line)
        if match:
            break
    else:
        return []

    indent = len(match.group(1))
    patterns = []
    for line in lines[index + 1:]:
        if not line.strip():
            continue
        if len(line) - len(line.lstrip()) <= indent and not line.lstrip().startswith("#"):
            break
        found = re.match(r"\s*-\s*'([^']+)'", line)
        if found:
            patterns.append(found.group(1))
    return patterns


def covers(pattern: str, directory: str) -> bool:
    """Whether one filter pattern reaches anything under a directory.

    Prefix matching, and that is §15.1's own model rather than a loose reading
    of it. `src/Services` is a child of `src` and NO filter names it: it is
    "spoken for by its siblings' filters", which is exactly why that section
    requires the children of `src/Services` to be checked separately. A rule
    demanding `src/Services/**` would fail on the correct workflow.

    **The reach and its cost, stated rather than implied.** A pattern naming
    one path INSIDE a deployable satisfies this — `src/Gateway/Program.cs`
    would mark `src/Gateway` covered. So what this proves is that every
    deployable is reachable by some filter, not that any filter is complete;
    the second is §15.1's "if changing a file can change what a service ships,
    that file belongs in that service's filter", and no check here reaches it.
    The escape needs somebody to have written a file-level pattern where every
    existing one is a `/**`, which is visible in review in a way a missing
    entry never was.

    A negation covers nothing: `!deploy/compose/**` is there to carve out, and
    reading it as coverage would let an exclusion satisfy the inventory.
    """
    if pattern.startswith("!"):
        return False
    return pattern == f"{directory}/**" or pattern.startswith(f"{directory}/")


def check_filters(root: Path = ROOT) -> list[str]:
    problems: list[str] = []

    try:
        text = WORKFLOW.read_text(encoding="utf-8")
    except OSError as error:
        return [f".github/workflows/ci.yml is not readable: {error}"]

    patterns = read_filters(text)

    # The gate's own subject, first. Every check below is "is this directory in
    # that list", so an empty list passes all of them and reports a clean
    # inventory -- which CLAUDE.md names as this repository's most-repeated
    # failure. A parser that extracted nothing is the parser being broken, not
    # the workflow being complete.
    if not patterns:
        return [
            "found no path patterns under `filters: |` in ci.yml. Every check "
            "here asks whether a directory appears in that list, so an empty "
            "list would pass all of them vacuously -- this is the parser "
            "failing, not the inventory being complete"
        ]

    # §15.1: "Without this, negated patterns are silently ignored: the default
    # quantifier ('some') never evaluates the exclusion below." The `deploy`
    # filter excludes deploy/compose/** on the stated grounds that a
    # compose-only change reaches no cluster and must not roll one. Without the
    # quantifier that exclusion is decoration, and nothing else would say so.
    if "some-with-excludes" not in text:
        problems.append(
            "the paths-filter step does not set predicate-quantifier: "
            "'some-with-excludes'. The default quantifier never evaluates a "
            "negated pattern, so `!deploy/compose/**` would be silently "
            "ignored and a compose-only change would deploy (§15.1)"
        )

    for parent in ("src", "src/Services"):
        directory = root / parent
        if not directory.is_dir():
            problems.append(f"{parent} is not a directory; the inventory cannot be checked")
            continue

        children = sorted(child.name for child in directory.iterdir() if child.is_dir())
        if not children:
            problems.append(
                f"{parent} has no subdirectories, so its half of this check "
                "would pass vacuously"
            )
            continue

        for child in children:
            path = f"{parent}/{child}"
            if not any(covers(pattern, path) for pattern in patterns):
                problems.append(
                    f"{path} is matched by no filter in ci.yml. §15.1 requires "
                    "every immediate child of src/ and of src/Services/ to "
                    "appear in at least one, because a deployable nothing "
                    "filters is a deployable CI never rebuilds"
                )

    return problems


# --------------------------------------------------------------------------
# images
# --------------------------------------------------------------------------

def read_image_matrix(workflow_text: str) -> list[tuple[str, str]]:
    """The `images` job's matrix, as (filter, dockerfile) pairs.

    Positional rather than block-scoped: the matrix entries are the only place
    in this workflow where `filter:` and `dockerfile:` appear together, and
    pairing them by adjacency is what lets this stay a text match. A `filter:`
    with no `dockerfile:` after it is dropped, which the count check below
    turns into a failure rather than a silence.
    """
    pairs = []
    pending: str | None = None
    for line in workflow_text.splitlines():
        if found := re.match(r"\s*-?\s*filter:\s*(\S+)\s*$", line):
            pending = found.group(1)
        elif found := re.match(r"\s*dockerfile:\s*(\S+)\s*$", line):
            if pending is not None:
                pairs.append((pending, found.group(1)))
                pending = None
    return pairs


def check_images(root: Path = ROOT) -> list[str]:
    """Every image is built, and everything built is an image.

    Both directions, on `deploy/observability/check.py`'s reasoning about
    alerts and runbooks: a Dockerfile in no matrix is an image CI never builds,
    and a matrix entry naming no Dockerfile is a step that fails on the day it
    is first selected -- which, being path-filtered, may be months after the
    rename that caused it.

    The third check is the one neither direction can make. A matrix entry's
    `filter` is read back as `needs.changes.outputs[matrix.filter]`, and an
    expression indexing a name no filter defines evaluates to the empty string
    rather than erroring -- so the `if` is false, the step is skipped, and the
    job reports success having built nothing. A misspelling there is invisible
    from both sides of the inventory.
    """
    problems: list[str] = []

    try:
        text = WORKFLOW.read_text(encoding="utf-8")
    except OSError as error:
        return [f".github/workflows/ci.yml is not readable: {error}"]

    matrix = read_image_matrix(text)
    if not matrix:
        return [
            "found no (filter, dockerfile) pairs in the images matrix in "
            "ci.yml. Every check here compares against that list, so an empty "
            "one passes them all vacuously -- the parser is what is broken"
        ]

    declared = {dockerfile for _filter, dockerfile in matrix}

    # Forward: every application Dockerfile is built by some entry.
    on_disk = {
        str(path.relative_to(root)).replace("\\", "/")
        for path in (root / "src").rglob("Dockerfile")
    }
    if not on_disk:
        problems.append(
            "found no Dockerfile under src/: the forward check would pass "
            "vacuously, so the search is what is broken"
        )
    for dockerfile in sorted(on_disk - declared):
        problems.append(
            f"{dockerfile} is built by no entry in ci.yml's images matrix. "
            "§15.1 builds every image a changed service ships, and one nothing names "
            "is one CI never builds -- which surfaces on the next compose pull "
            "request rather than on the change that broke it"
        )

    # Reverse: every entry names a Dockerfile that is there.
    for dockerfile in sorted(declared - on_disk):
        problems.append(
            f"ci.yml's images matrix names {dockerfile}, which does not exist. "
            "The step is path-filtered, so this fails on the day it is next "
            "selected and not on the change that renamed it"
        )

    # And the filter each entry reads back has to be one that exists.
    defined = set(re.findall(r"^\s{12}([a-z][a-z0-9-]*):\s*$", text, re.MULTILINE))
    if not defined:
        problems.append(
            "found no filter names in the paths-filter block: the check below "
            "would pass vacuously"
        )
    for name in sorted({filter_name for filter_name, _ in matrix} - defined):
        problems.append(
            f"ci.yml's images matrix reads the filter {name!r}, which the "
            "paths-filter step does not define. A GitHub expression indexing a "
            "missing output is the empty string, so that entry's `if` is "
            "always false and the job reports success having built nothing"
        )

    return problems


# --------------------------------------------------------------------------
# stages
# --------------------------------------------------------------------------

def read_stage(directory: Path) -> tuple[int, set[str], set[str]]:
    """One stage's total, its test assemblies and its test identities.

    `dotnet test` over a solution writes one TRX per test project, so this sums
    rather than reads a single number. A stage directory with no TRX in it is
    not zero tests -- it is a logger that did not run, and the caller treats
    the two differently.
    """
    total = 0
    assemblies: set[str] = set()
    identities: set[str] = set()

    files = sorted(directory.rglob("*.trx"))
    if not files:
        raise SystemExit(
            f"no *.trx under {directory}. The stage did not run, or --logger trx "
            "and --results-directory disagree about where to write."
        )

    for path in files:
        try:
            root = ElementTree.parse(path).getroot()
        except ElementTree.ParseError as error:
            raise SystemExit(f"{path} is not readable as XML: {error}") from error

        # TRX carries a default namespace, so tags arrive as
        # `{...}Counters`. Matching on the local name keeps this working if the
        # schema URL is ever versioned.
        for element in root.iter():
            tag = element.tag.rsplit("}", 1)[-1]
            if tag == "Counters":
                total += int(element.get("total", "0"))
            elif tag == "TestMethod":
                # The assembly is part of the identity, not just of the
                # inventory. Two projects may hold a class of the same name --
                # `ArchitectureTests` is very nearly that already -- and an
                # identity without it would report an overlap that is not
                # there, which is a red build with no defect behind it.
                code_base = element.get("codeBase", "")
                assembly = Path(code_base.replace("\\", "/")).stem if code_base else ""
                if assembly:
                    assemblies.add(assembly)
                identities.add(
                    f"{assembly}!{element.get('className', '')}.{element.get('name', '')}"
                )

    return total, assemblies, identities


def test_projects(solution: Path = SOLUTION) -> set[str]:
    """Every test project in the solution, by assembly name.

    `EndsWith("Tests")` and not `".Tests"`, exactly as `Directory.Build.props`
    scopes its CA1707 carve-out and for the same reason:
    `Platform.IntegrationTests` ends with the word and not the dotted suffix.
    A `.TestSupport` library is not a test project (§4.1) and is excluded by
    the same predicate without needing to be named.
    """
    text = solution.read_text(encoding="utf-8")
    return {
        name
        for name in re.findall(r'Path="[^"]*?([A-Za-z0-9.]+)\.csproj"', text)
        if name.endswith("Tests")
    }


def check_stages(directories: list[Path], solution: Path = SOLUTION) -> list[str]:
    problems: list[str] = []
    seen: dict[str, set[str]] = {}
    covered: set[str] = set()

    for directory in directories:
        stage = directory.name
        if stage not in STAGE_FLOORS:
            problems.append(
                f"{directory} is not one of the stages this gate knows "
                f"({', '.join(sorted(STAGE_FLOORS))}). A results directory nobody "
                "declared is a stage with no floor, which is the check not running"
            )
            continue

        total, assemblies, identities = read_stage(directory)
        seen[stage] = identities
        covered |= assemblies

        if total == 0:
            problems.append(
                f"the {stage} stage ran no tests. `dotnet test` exits zero on a "
                "filter that selects nothing (§12.1), so a green step says "
                "nothing about whether this stage exists"
            )
        elif total < STAGE_FLOORS[stage]:
            problems.append(
                f"the {stage} stage ran {total} tests, below its floor of "
                f"{STAGE_FLOORS[stage]}. Either the filter stopped selecting what "
                "it used to, or the floor in pipeline_gate.py is owed a "
                "deliberate edit"
            )

    if not seen:
        return problems + [
            "no stage was read, so every check here passed vacuously"
        ]

    # Structural half 1: the stages partition the suite rather than sampling it.
    expected = test_projects(solution)
    if not expected:
        problems.append(
            "found no test projects in Platform.slnx: the check below would "
            "pass vacuously, so the parser is what is broken"
        )
    for project in sorted(expected - covered):
        problems.append(
            f"{project} ran in no stage. A test project the staged pipeline "
            "never selects is a suite that stopped running without anything "
            "going red"
        )

    # Structural half 2: and they do not overlap.
    #
    # ci.yml claims the filters are "exhaustive and disjoint by construction".
    # This is what makes the second half of that a check: an overlap is the
    # slow half of the suite paid for twice, and on the integration stage that
    # is a container set.
    stages = sorted(seen)
    for index, first in enumerate(stages):
        for second in stages[index + 1:]:
            both = seen[first] & seen[second]
            if both:
                sample = ", ".join(identity.replace("!", " in ") for identity in sorted(both)[:3])
                problems.append(
                    f"{len(both)} test(s) ran in both the {first} and {second} "
                    f"stages, for example {sample}. The stage filters are "
                    "supposed to be disjoint, and an overlap is the suite paid "
                    "for twice"
                )

    return problems


# --------------------------------------------------------------------------
# CLI
# --------------------------------------------------------------------------

def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)

    sub.add_parser("filters", help="every deployable is matched by some path filter")
    sub.add_parser("images", help="every Dockerfile is built by some matrix entry")

    stages = sub.add_parser("stages", help="every stage ran, ran enough, and ran once")
    stages.add_argument("results", nargs="+", type=Path)

    args = parser.parse_args(argv[1:])

    if args.command == "filters":
        problems = check_filters()
        if code := fail(problems, "the path filters in ci.yml"):
            return code
        print("pipeline-gate: every deployable under src/ is matched by a filter.")
        return 0

    if args.command == "images":
        problems = check_images()
        if code := fail(problems, "the images matrix in ci.yml"):
            return code
        print("pipeline-gate: every Dockerfile under src/ is built by a matrix entry.")
        return 0

    problems = check_stages(args.results)
    if code := fail(problems, "the test stages"):
        return code
    print(f"pipeline-gate: {len(args.results)} stages ran, none empty, none overlapping.")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
