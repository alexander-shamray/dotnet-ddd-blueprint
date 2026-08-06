#!/usr/bin/env python3
"""Fail the build on a package pin whose licence has not been cleared.

Appendix B is the register of what is cleared. `Directory.Packages.props` is
what CI will actually restore. Section 4.4 asks for a check that the two agree,
and this is it: a package in the props file and not the register is how a
licence obligation gets acquired by a restore rather than by a decision.

It reads both files as text and resolves nothing over the network, which is why
Section 15.1 can put it ahead of the build fork. Nothing here needs a restore,
and the scan that catches a licence obligation is cheapest before anything has
compiled.
"""

from __future__ import annotations

import argparse
import re
import sys
import xml.etree.ElementTree as ElementTree
from pathlib import Path

GATE_DIR = Path(__file__).resolve().parent
REPO_ROOT = GATE_DIR.parents[1]

DEFAULT_PINS = REPO_ROOT / "Directory.Packages.props"
DEFAULT_REGISTER = REPO_ROOT / "docs" / "backend-architecture" / "appendix-b-licences.md"
DEFAULT_CHAPTER = REPO_ROOT / "docs" / "backend-architecture" / "04-solution-structure.md"
DEFAULT_ALLOWED = GATE_DIR / "allowed-licences.txt"

# The register has three tables. Only the first clears anything — the other two
# record what was avoided and what still needs review, and a pin matching either
# of those has no business being in the props file to begin with.
CHOSEN_HEADING = "## Chosen — free for commercial use"

# Section 4.4 names three classes of register row that will never carry a pin.
# A check that does not know them reports false positives until somebody stops
# reading its output, so each is encoded here rather than guessed at.
#
# Infrastructure products need no entry. Keycloak, RabbitMQ, SQL Server and
# Redis are named as products and carry no package identity, so they never enter
# this check at all — which is precisely what Section 4.4 means by "match on
# package identity, never on the product a package is named after".

# Rows skipped whole. Both Aspire rows are deliberately unpinned (Section 4.4,
# Section 14.2): their licences are cleared ahead of a decision not yet taken.
UNPINNED_ROWS = frozenset({"Aspire.Hosting.*", "Aspire.*"})

# Identities cleared as the unchosen half of an either/or row. Clearing a
# licence for an alternative is not a commitment to restore it.
UNPINNED_ALTERNATIVES = frozenset({"AwesomeAssertions"})

# The register writes licences the way prose does; the allow-list writes SPDX.
# One spelling map, applied in one direction. An unmapped spelling falls through
# unchanged and then fails the allow-list check, which is the safe direction:
# a licence this gate cannot name is a licence it must not clear.
SPDX = {
    "MIT": "MIT",
    "Apache 2.0": "Apache-2.0",
    "BSD-3": "BSD-3-Clause",
    "MPL 2.0": "MPL-2.0",
}


def read_pins(path: Path) -> set[str]:
    """Every package identity pinned in Directory.Packages.props.

    The DOCTYPE check is not ceremony. Both attacks that stdlib ElementTree is
    exposed to — entity expansion and quadratic blowup — need a DTD to declare
    the entity, and an MSBuild props file has no legitimate use for one. Refusing
    the declaration outright is cheaper than taking a defusedxml dependency on a
    gate whose whole argument is that it runs anywhere with nothing installed.
    """
    text = path.read_text(encoding="utf-8")
    if "<!DOCTYPE" in text or "<!ENTITY" in text:
        raise ValueError(f"{path} declares a DTD, which a props file has no reason to")

    root = ElementTree.fromstring(text)
    return {element.attrib["Include"] for element in root.iter("PackageVersion")}


def read_register(path: Path) -> list[tuple[list[str], str]]:
    """The Chosen table as (identities, licence cell) pairs, in file order."""
    rows: list[tuple[list[str], str]] = []
    in_section = False

    for line in path.read_text(encoding="utf-8").splitlines():
        if line.startswith("## "):
            in_section = line.strip() == CHOSEN_HEADING
            continue
        if not in_section or not line.startswith("|"):
            continue

        cells = [cell.strip() for cell in line.strip().strip("|").split("|")]
        if len(cells) < 2 or cells[0] == "Package":
            continue
        if set(cells[0]) <= {"-", ":"}:
            continue

        rows.append((re.findall(r"`([^`]+)`", cells[0]), cells[1]))

    return rows


def read_versions(text: str) -> dict[str, str]:
    """Every Include/Version pair in a props document, by identity."""
    return dict(re.findall(r'Include="([^"]+)"\s+Version="([^"]+)"', text))


def read_chapter_sample(path: Path) -> str:
    """Section 4.4's fenced transcription of Directory.Packages.props.

    Identified by content rather than by position, so inserting a chapter or
    another XML block above it does not silently start comparing the wrong one.
    """
    for block in re.findall(r"```xml\r?\n(.*?)```", path.read_text(encoding="utf-8"), re.S):
        if "ManagePackageVersionsCentrally" in block:
            return block
    return ""


def compare_sample(props_text: str, sample: str) -> list[str]:
    """Disagreements between the props file and the chapter that prints it.

    Reported against the chapter. The props file is what CI restores, so it is
    the side the rest of the system depends on; the sample is what a reader
    believes, which is the half that can be wrong without anything breaking.
    """
    if not sample:
        return ["Section 4.4 has no central package management sample to compare against"]

    actual, printed = read_versions(props_text), read_versions(sample)
    findings = []
    for identity in sorted(set(actual) | set(printed)):
        if actual.get(identity) != printed.get(identity):
            findings.append(
                f"{identity}: props pins {actual.get(identity) or 'nothing'}, "
                f"Section 4.4 prints {printed.get(identity) or 'nothing'}")
    return findings


def read_allowed(path: Path) -> set[str]:
    lines = path.read_text(encoding="utf-8").splitlines()
    return {line.strip() for line in lines if line.strip() and not line.startswith("#")}


def spdx(licence_cell: str) -> list[str]:
    """Split a register licence cell into SPDX identifiers.

    A cell reading "Apache 2.0 / BSD-3" is one package offered under either.
    """
    parts = [part.strip() for part in licence_cell.split("/")]
    return [SPDX.get(part, part) for part in parts if part]


def audit(pins: set[str], rows: list[tuple[list[str], str]], allowed: set[str]) -> list[str]:
    """Every disagreement between the pins and the register, worst first."""
    registered: dict[str, list[str]] = {}
    for identities, licence_cell in rows:
        if UNPINNED_ROWS.intersection(identities):
            continue
        for identity in identities:
            registered[identity] = spdx(licence_cell)

    unregistered: list[str] = []
    forbidden: list[str] = []
    for pin in sorted(pins):
        licences = registered.get(pin)
        if licences is None:
            unregistered.append(
                f"{pin} is pinned and absent from Appendix B: its licence has never been cleared")
            continue
        if not any(licence in allowed for licence in licences):
            forbidden.append(
                f"{pin} is registered as {' / '.join(licences)}, which is outside the allow-list")

    stale: list[str] = []
    for identity in sorted(registered):
        if identity in pins or identity in UNPINNED_ALTERNATIVES:
            continue
        stale.append(
            f"{identity} is registered in Appendix B and pinned nowhere: a dropped pin, "
            f"or a row that outlived its dependency")

    return unregistered + forbidden + stale


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--pins", type=Path, default=DEFAULT_PINS)
    parser.add_argument("--register", type=Path, default=DEFAULT_REGISTER)
    parser.add_argument("--chapter", type=Path, default=DEFAULT_CHAPTER)
    parser.add_argument("--allowed", type=Path, default=DEFAULT_ALLOWED)
    args = parser.parse_args(argv)

    pins = read_pins(args.pins)
    rows = read_register(args.register)
    findings = audit(pins, rows, read_allowed(args.allowed))
    findings += compare_sample(
        args.pins.read_text(encoding="utf-8"), read_chapter_sample(args.chapter))

    if findings:
        print(f"Licence gate: {len(findings)} finding(s) across {len(pins)} pinned package(s).\n")
        for finding in findings:
            print(f"  {finding}")
        print(f"\nReconcile {args.pins.name}, {args.register.name} and {args.chapter.name}"
              f" in the same change.")
        return 1

    # Output stays ASCII. A gate whose job is to report a failure must not be the
    # thing that fails, and stdout encoding on a runner is not ours to assume.
    print(f"Licence gate: {len(pins)} pinned package(s). Every one registered, "
          f"licence-cleared, and printed correctly in {args.chapter.name}.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
