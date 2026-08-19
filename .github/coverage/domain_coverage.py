#!/usr/bin/env python3
"""Print the domain layer's coverage from a Cobertura report, and nothing else.

Section 12.9 calls coverage a diagnostic rather than a target, so this reports and
never gates: no threshold, and no non-zero exit on a low figure.  A threshold
that fails a build is PR-25's quality gates.

It does exit non-zero on a **missing or unreadable** report, which is a
different claim: a coverage step that shrugs at no data prints nothing on the
day the collector stops running, and nothing reads exactly like a clean result.

Stdlib only, like the licence gate one directory over, and for the same reason
-- it runs in CI ahead of any restore.  That rules out `defusedxml`, which is
the usual answer to `ElementTree`'s entity-expansion exposure; what makes the
stdlib parser acceptable here is the input rather than the parser.  This reads
one artefact that `Microsoft.CodeCoverage` wrote, on the runner, seconds
earlier, from a path this step names -- there is no untrusted document on that
path, and a repository that could plant one could plant the script instead.

**It has no test suite, deliberately.**  The licence gate has one because it is
a gate whose carve-outs are branches only a negative case exercises; this
asserts nothing about the repository.  Its one real failure mode is having no
report to read, and that is checked here at run time rather than in a suite.
"""
import os
import sys
import xml.etree.ElementTree as ElementTree
from pathlib import Path


def find_report(results_root: Path) -> Path:
    """The single Cobertura file a solution-wide run produces.

    The Code Coverage collector merges every test project's data into one
    attachment per run, so more than one file here means the run was not the
    one this step is written for -- two invocations of `dotnet test`, or a
    stale TestResults directory left by a previous one.  Either way the figure
    would be about an unknown subset of the suite, so say so rather than pick.
    """
    reports = sorted(results_root.rglob("*.cobertura.xml"))
    if not reports:
        raise SystemExit(
            f"no *.cobertura.xml under {results_root}. The collector did not run, "
            "or --results-directory pointed somewhere else."
        )
    if len(reports) > 1:
        joined = "\n  ".join(str(report) for report in reports)
        raise SystemExit(
            f"{len(reports)} coverage reports under {results_root}, expected one:\n  {joined}"
        )
    return reports[0]


def render(root: ElementTree.Element) -> str:
    """The summary a human reads, in GitHub's markdown."""
    covered = int(root.get("lines-covered", "0"))
    valid = int(root.get("lines-valid", "0"))
    if valid == 0:
        raise SystemExit(
            "the report covers no lines. The ModulePaths filter in "
            "coverage.runsettings matched no assembly, which is what a renamed "
            "or removed Domain project looks like from here."
        )

    lines = [
        "## Domain-layer coverage",
        "",
        f"**{covered / valid:.1%}** of {valid} lines, over every `*.Domain` assembly in the run.",
        "",
        "| Assembly | Line rate |",
        "|---|---|",
    ]
    for package in sorted(root.iter("package"), key=lambda element: element.get("name", "")):
        rate = float(package.get("line-rate", "0"))
        lines.append(f"| `{package.get('name')}` | {rate:.1%} |")

    lines += [
        "",
        "Reported, not gated (Section 12.9). The filter is `.*\\.Domain\\.dll$` in "
        "`coverage.runsettings`; see `docs/testing.md`.",
    ]
    return "\n".join(lines)


def main(argv: list[str]) -> int:
    report = find_report(Path(argv[1] if len(argv) > 1 else "TestResults"))

    try:
        root = ElementTree.parse(report).getroot()
    except ElementTree.ParseError as error:
        raise SystemExit(f"{report} is not readable as XML: {error}") from error

    summary = render(root)
    print(summary)

    if step_summary := os.environ.get("GITHUB_STEP_SUMMARY"):
        with open(step_summary, "a", encoding="utf-8") as handle:
            handle.write(summary + "\n")

    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
