#!/usr/bin/env python3
"""Print the domain layer's coverage from every Cobertura report of a run.

Section 12.9 calls coverage a diagnostic rather than a target, so this reports and
never gates: no threshold, and no non-zero exit on a low figure.  **PR-25 took
that decision rather than inheriting it** — the same section says a threshold
that fails a build stops being read and starts being satisfied, and PR-25's
quality gate is the stage-count floor in `.github/pipeline-gate/` instead, whose
subject is whether a suite ran at all.

It does exit non-zero on a **missing or unreadable** report, which is a
different claim: a coverage step that shrugs at no data prints nothing on the
day the collector stops running, and nothing reads exactly like a clean result.

**It merges, and PR-25 is why.**  Section 15.1's pipeline runs the unit and
integration stages separately (docs/testing.md), so there is no longer one run
to read: the domain assemblies are exercised on both sides of
`Category=Integration`, and section 12.9 asks for the figure "over the whole
run" rather than over whichever half was instrumented last.  Measured on this
repository, the unit stage covers 253 of 308 method lines and the integration
stage 192; the union is 257, so four lines are reached only by tests that need
a container and a per-stage figure would under-report the thing it is named
after.

Two facts about the artefacts, both measured rather than assumed, decide how
the merge is written:

* `lines-valid` and `lines-covered` count the lines under
  `class/methods/method/lines`, NOT the ones under `class/lines`.  On this
  repository the first is 308 and the second 247, and only the first reproduces
  the collector's own totals exactly.  A merge keyed on the wrong one would
  print a plausible number that no single-stage run agrees with.
* `--logger trx` changes the layout.  Without it the collector leaves one
  merged attachment per run, which is what the single-file reader this replaces
  relied on; with it, each test project also writes its own partial attachment
  under the TRX result directory — eight files for one stage here, three of
  them empty.  The stage-count gate needs those TRX files, so the reporter had
  to stop assuming.

**The union is what makes both of those safe.**  Hits are merged with `max`
over an injective key, so reading the same attachment twice — which the layout
above guarantees, since the merged file and the per-project ones overlap —
cannot inflate the figure.  Summing hits would have.

Stdlib only, like the licence gate one directory over, though **not** for the
licence gate's reason: that one runs ahead of the build, and this one cannot —
it reads what the test run produced, so restore and build are behind it either
way.  What it inherits is only the preference for adding no dependency.  That
rules out `defusedxml`, which is the usual answer to `ElementTree`'s
entity-expansion exposure; what makes the stdlib parser acceptable here is the
input rather than the parser.  This reads
artefacts that `Microsoft.CodeCoverage` wrote, on the runner, minutes earlier,
under paths this step names — there is no untrusted document on that path, and
a repository that could plant one could plant the script instead.

**It has a test suite now, and the reason it did not is what changed.**  The
old argument was that it asserts nothing about the repository and its one real
failure mode — no report — is checked at run time.  Both halves still hold.
What arrived with the merge is arithmetic: a key that collides, a `max` that
should have been a sum, a partial attachment counted as a whole.  None of those
fails loudly, and all of them move the number.  A figure that is quietly wrong
is worse than no figure, because it is read.

    python .github/coverage/domain_coverage.py TestResults/unit TestResults/integration
"""
import os
import sys
import xml.etree.ElementTree as ElementTree
from pathlib import Path

# One method line, identified the way the collector identifies it.
#
# The signature is in the key because overloads share a name, and the class is
# because a partial class's members are spread across files — both measured to
# collide without it on this repository's own report.  With all five parts the
# key is injective: 308 keys for 308 lines, checked against the collector's own
# `lines-valid` rather than assumed.
LineKey = tuple[str, str, str, str, str]


def find_reports(roots: list[Path]) -> list[Path]:
    """Every Cobertura file under the given directories.

    A stage that produced none is a stage whose collector did not run, and it
    is named individually: "no coverage anywhere" and "the integration stage
    collected nothing" are different defects, and the second is invisible in a
    total.
    """
    reports: list[Path] = []
    for root in roots:
        found = sorted(root.rglob("*.cobertura.xml"))
        if not found:
            raise SystemExit(
                f"no *.cobertura.xml under {root}. The collector did not run for "
                "that stage, or --results-directory pointed somewhere else."
            )
        reports += found
    return reports


def merge(reports: list[Path]) -> dict[LineKey, int]:
    """Union the reports, keeping the highest hit count seen for each line.

    `max` rather than `+` is the whole of the de-duplication story.  The
    layout puts the same line in more than one file by construction — the
    run's merged attachment and the per-project one that fed it — so summing
    would count a line twice for having been reported twice, and the figure
    would grow with the number of test projects rather than with the tests.
    """
    hits: dict[LineKey, int] = {}
    for report in reports:
        try:
            root = ElementTree.parse(report).getroot()
        except ElementTree.ParseError as error:
            raise SystemExit(f"{report} is not readable as XML: {error}") from error

        for package in root.iter("package"):
            package_name = package.get("name", "")
            for klass in package.iter("class"):
                class_name = klass.get("name", "")
                for method in klass.iter("method"):
                    for line in method.iter("line"):
                        key: LineKey = (
                            package_name,
                            class_name,
                            method.get("name", ""),
                            method.get("signature", ""),
                            line.get("number", ""),
                        )
                        hits[key] = max(hits.get(key, 0), int(line.get("hits", "0")))
    return hits


def render(hits: dict[LineKey, int], reports: list[Path]) -> str:
    """The summary a human reads, in GitHub's markdown."""
    if not hits:
        raise SystemExit(
            f"the {len(reports)} report(s) cover no lines. The ModulePaths filter "
            "in coverage.runsettings matched no assembly, which is what a renamed "
            "or removed Domain project looks like from here."
        )

    valid = len(hits)
    covered = sum(1 for count in hits.values() if count > 0)

    per_package: dict[str, list[int]] = {}
    for (package, *_rest), count in hits.items():
        totals = per_package.setdefault(package, [0, 0])
        totals[1] += 1
        if count > 0:
            totals[0] += 1

    lines = [
        "## Domain-layer coverage",
        "",
        # ASCII in the printed body on purpose. The step summary is written as
        # UTF-8, but stdout takes the console's code page, and a developer
        # running this in a cp1252 terminal should not meet a
        # UnicodeEncodeError from a reporting step.
        f"**{covered / valid:.1%}** of {valid} lines, over every `*.Domain` assembly "
        f"in the run - the union of {len(reports)} report(s) across every stage.",
        "",
        "| Assembly | Line rate |",
        "|---|---|",
    ]
    for package in sorted(per_package):
        package_covered, package_valid = per_package[package]
        lines.append(f"| `{package}` | {package_covered / package_valid:.1%} |")

    lines += [
        "",
        "Reported, not gated (Section 12.9). The filter is `.*\\.Domain\\.dll$` in "
        "`coverage.runsettings`; see `docs/testing.md`.",
    ]
    return "\n".join(lines)


def main(argv: list[str]) -> int:
    roots = [Path(argument) for argument in argv[1:]] or [Path("TestResults")]
    reports = find_reports(roots)
    summary = render(merge(reports), reports)
    print(summary)

    if step_summary := os.environ.get("GITHUB_STEP_SUMMARY"):
        with open(step_summary, "a", encoding="utf-8") as handle:
            handle.write(summary + "\n")

    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
