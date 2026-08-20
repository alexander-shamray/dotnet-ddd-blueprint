#!/usr/bin/env python3
"""Gate for deploy/observability and docs/runbooks.

§13.9 requires every alert to have a runbook and every runbook to have an
alert — "checkable in both directions: an alert with no runbook is a 3 a.m.
page with no procedure, and a runbook with no alert is a procedure nobody will
be told to follow." That is checks 1 and 2 below.

§13.6 adds a third leg: "An alert has three parts: a condition, a signal and a
procedure." Two of the alerts in that chapter were written against signals that
did not exist, and both looked correct — the dashboard is empty either way,
whether the system is healthy or the metric was never published. Checks 4 and 5
are that leg, in both directions:

  * every metric a LOADED rule reads is published by something;
  * every metric an AWAITING-SIGNAL rule reads is published by nothing.

The second is what makes awaiting-signal.yaml self-clearing. The day somebody
ships the missing instrument, this gate goes red and names the rule to move —
an "awaiting signal" list that nothing re-checks would quietly become a list of
alerts nobody ever turned on.

Check 6 is the gate's own subject: a parser that silently extracted nothing
would pass 4 and 5 vacuously, which is this repository's most-repeated failure.

Stdlib only, on the licence gate's terms: no restore, no dependencies, and it
runs before anything is built.

    py -3.12 deploy/observability/check.py
"""

from __future__ import annotations

import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
OBSERVABILITY = ROOT / "deploy" / "observability"
RUNBOOKS = ROOT / "docs" / "runbooks"
WORKFLOW = ROOT / ".github" / "workflows" / "observability.yml"

LOADED_RULES = OBSERVABILITY / "alerts" / "platform-alerts.yaml"
AWAITING_RULES = OBSERVABILITY / "alerts" / "awaiting-signal.yaml"

# EVERY PATH OUTSIDE deploy/observability THAT THIS SCRIPT READS, declared once.
#
# The workflow's path filter must cover each of them, or a change to one is a
# green pull request that skips the gate watching it. deploy/helm/smoke.sh
# learned this three times over and ended it the same way: declare the list
# beside the reads and assert the other copy matches. Check 7 is that assertion.
SOURCE_INPUTS = [
    "src",
    "docs/runbooks",
]

# The one file under docs/runbooks that is not a runbook. Declared rather than
# pattern-matched, so a second non-runbook file has to be argued for.
NOT_A_RUNBOOK = {"README.md"}

# Metrics that no C# file declares because nothing in this solution declares
# them — each is published by an exporter or an instrumentation package that is
# part of the target deployment. A name that is neither found in C# nor listed
# here is a typo, which is the entire point of keeping this list short.
EXTERNAL_METRICS = {
    "http_server_request_duration_seconds":
        "ASP.NET Core instrumentation, enabled in §13.2",
    "rabbitmq_queue_messages":
        "the RabbitMQ exporter — §14.1's broker, §13.6's error-queue alert",
    "kube_job_status_failed":
        "kube-state-metrics — §7.4's migration hook Job",
}

# PromQL keywords that survive the stripping below and are not metric names.
PROMQL_KEYWORDS = {
    "and", "or", "unless", "by", "without", "on", "ignoring",
    "group_left", "group_right", "offset", "bool", "le", "inf", "nan",
    "start", "end",
}

# Suffixes the OpenTelemetry Prometheus exporter appends. Stripped
# progressively when matching a PromQL name back to a C# instrument, because
# `request.duration` with unit `s` is exported as
# `request_duration_seconds_bucket`.
SUFFIXES = ("_bucket", "_count", "_sum", "_total", "_seconds", "_bytes", "_ratio")

# The generic argument is OPTIONAL, and that is not tidiness. CreateHistogram
# and CreateCounter are always written `<double>` / `<long>`, but
# CreateObservableGauge infers its type from the callback and is written with no
# type argument at all — so a pattern requiring `<...>` finds the histograms,
# finds the counters, and silently misses every gauge §13.6 added. It did, on
# the first run of this gate, and the symptom was four correct alerts reported
# as having no signal.
INSTRUMENT = re.compile(
    r"Create(?:Observable)?(?:Histogram|Counter|Gauge|UpDownCounter)"
    r"\s*(?:<[^>]+>)?\s*\(\s*\"([^\"]+)\"")

failures: list[str] = []


def fail(message: str) -> None:
    failures.append(message)


# ----------------------------------------------------------------- parsing --


def read(path: Path) -> str:
    # newline="" is deliberately absent: this reads text and never asserts on
    # line endings, so universal newlines is what is wanted. .gitattributes
    # pins this tree to LF for the tools that DO care.
    return path.read_text(encoding="utf-8")


def parse_rules(path: Path) -> list[dict[str, object]]:
    """Extract alert name, expression and runbook from a Prometheus rules file.

    A regex rather than a YAML parser, because the stdlib has none and this
    gate refuses dependencies for the reason the licence gate does. The shape
    it accepts is narrow — `- alert:`, `expr:` and `runbook_url:` — and check 6
    fails loudly if a rule comes back without an expression, so a file this
    cannot read is a red gate rather than a silent pass.
    """
    rules: list[dict[str, object]] = []
    current: dict[str, object] | None = None
    expr_lines: list[str] = []
    in_expr = False

    for raw in read(path).splitlines():
        line = raw.rstrip()
        stripped = line.strip()

        match = re.match(r"-\s*alert:\s*(\S+)", stripped)
        if match:
            if current is not None:
                current["expr"] = " ".join(expr_lines)
                rules.append(current)
            current = {"alert": match.group(1), "expr": "", "runbook": None}
            expr_lines = []
            in_expr = False
            continue

        if current is None or stripped.startswith("#"):
            continue

        match = re.match(r"expr:\s*(.*)$", stripped)
        if match:
            rest = match.group(1).strip()
            if rest in ("|", ">-", ">", "|-"):
                in_expr = True
            else:
                expr_lines.append(rest)
                in_expr = False
            continue

        match = re.match(r"runbook_url:\s*(\S+)", stripped)
        if match:
            current["runbook"] = match.group(1).strip("\"'")
            in_expr = False
            continue

        if in_expr:
            if re.match(r"[a-z_]+:\s", stripped) or stripped.startswith("- "):
                in_expr = False
            elif stripped:
                expr_lines.append(stripped)

    if current is not None:
        current["expr"] = " ".join(expr_lines)
        rules.append(current)

    return rules


def metrics_in(expression: str) -> set[str]:
    """Metric names referenced by a PromQL expression.

    Everything that is not a metric name is removed first — quoted strings,
    label matchers, durations, and the label lists after by/without/on/ignoring
    — and what survives that is an identifier not followed by `(`. Function
    names are excluded for free, because a function is always followed by one.
    """
    text = expression
    text = re.sub(r"\"[^\"]*\"|'[^']*'", " ", text)
    text = re.sub(r"\{[^}]*\}", " ", text)
    text = re.sub(r"\[[^\]]*\]", " ", text)
    text = re.sub(r"\b(?:by|without|on|ignoring|group_left|group_right)\s*\([^)]*\)", " ", text)

    found = set()
    for token in re.finditer(r"[A-Za-z_][A-Za-z0-9_]*", text):
        name = token.group(0)
        # The WHOLE remainder, left-stripped — not one character of it. Reading
        # a single character means `sum (…)`, with the `by (…)` group already
        # removed, looks like a metric called `sum`: the next character is a
        # space, and a one-character slice has nothing left after stripping it.
        if text[token.end():].lstrip().startswith("("):
            continue
        if name in PROMQL_KEYWORDS:
            continue
        found.add(name)

    return found


def declared_instruments() -> set[str]:
    """Instrument names declared in C#, with dots turned into underscores."""
    names = set()
    for source in (ROOT / "src").rglob("*.cs"):
        if "/obj/" in source.as_posix() or "/bin/" in source.as_posix():
            continue
        for match in INSTRUMENT.finditer(read(source)):
            names.add(match.group(1).replace(".", "_"))

    return names


def candidates(metric: str) -> set[str]:
    """A Prometheus name and every name it could have been exported from."""
    seen = {metric}
    queue = [metric]
    while queue:
        name = queue.pop()
        for suffix in SUFFIXES:
            if name.endswith(suffix):
                shorter = name[: -len(suffix)]
                if shorter and shorter not in seen:
                    seen.add(shorter)
                    queue.append(shorter)

    return seen


def is_published(metric: str, instruments: set[str]) -> bool:
    # Both sides take the same stripping. An exporter's metric carries the same
    # _count / _bucket / _sum suffixes a solution instrument's does, so matching
    # EXTERNAL_METRICS on the raw name alone would demand three entries per
    # histogram and reject the two that were not written down.
    reachable = candidates(metric)

    return bool(reachable & instruments) or bool(reachable & EXTERNAL_METRICS.keys())


# ------------------------------------------------------------------ checks --


def main() -> int:
    for path in (LOADED_RULES, AWAITING_RULES, RUNBOOKS, WORKFLOW):
        if not path.exists():
            fail(f"missing: {path.relative_to(ROOT).as_posix()}")

    if failures:
        return report()

    loaded = parse_rules(LOADED_RULES)
    awaiting = parse_rules(AWAITING_RULES)
    every_rule = loaded + awaiting
    instruments = declared_instruments()

    runbooks = {
        path.name for path in RUNBOOKS.glob("*.md") if path.name not in NOT_A_RUNBOOK
    }

    # 6. THE GATE'S OWN SUBJECT, asserted before anything that relies on it.
    #    A parser that quietly returned nothing would pass every check below
    #    vacuously.
    if not loaded:
        fail("parsed no rules from platform-alerts.yaml — the parser, not the file")
    if not awaiting:
        fail("parsed no rules from awaiting-signal.yaml — the parser, not the file")
    if not runbooks:
        fail("found no runbooks in docs/runbooks")
    if not instruments:
        fail("found no instruments in src/**/*.cs — the INSTRUMENT pattern matched nothing")

    for rule in every_rule:
        if not rule["expr"]:
            fail(f"{rule['alert']}: no expression parsed")
        elif not metrics_in(str(rule["expr"])):
            fail(f"{rule['alert']}: expression parsed but no metric names found in it")

    if failures:
        return report()

    # 1. Every alert names a runbook, and that runbook exists.
    claimed: dict[str, list[str]] = {}
    for rule in every_rule:
        runbook = rule["runbook"]
        if not runbook:
            fail(f"{rule['alert']}: no runbook_url")
            continue

        name = str(runbook).rsplit("/", 1)[-1]
        if name not in runbooks:
            fail(f"{rule['alert']}: runbook_url names {name}, which is not in docs/runbooks")
        claimed.setdefault(name, []).append(str(rule["alert"]))

    # 2. Every runbook is named by an alert — the other direction, and the one
    #    a reviewer never checks by hand.
    for name in sorted(runbooks - set(claimed)):
        fail(f"docs/runbooks/{name}: no alert points at it (§13.9)")

    for name, alerts in sorted(claimed.items()):
        if len(alerts) > 1:
            fail(f"docs/runbooks/{name}: claimed by more than one alert — {', '.join(alerts)}")

    # 3. No alert is in both files.
    both = {str(r["alert"]) for r in loaded} & {str(r["alert"]) for r in awaiting}
    for name in sorted(both):
        fail(f"{name}: defined in both platform-alerts.yaml and awaiting-signal.yaml")

    # 4. Every metric a LOADED rule reads is published by something.
    for rule in loaded:
        for metric in sorted(metrics_in(str(rule["expr"]))):
            if not is_published(metric, instruments):
                fail(
                    f"{rule['alert']}: reads `{metric}`, which no C# instrument declares "
                    f"and EXTERNAL_METRICS does not list. A loaded rule with no signal is "
                    f"silent, and silence reads as health (§13.6)")

    # 5. Every metric an AWAITING rule reads is published by NOTHING. This is
    #    the self-clearing half: it goes red on the day the instrument lands.
    for rule in awaiting:
        for metric in sorted(metrics_in(str(rule["expr"]))):
            if is_published(metric, instruments):
                fail(
                    f"{rule['alert']}: reads `{metric}`, which now HAS a signal. "
                    f"Move this rule from awaiting-signal.yaml into platform-alerts.yaml")

    # 6. Every metric a dashboard panel reads is published. A panel over a
    #    metric nothing publishes draws a flat empty line, which reads as "no
    #    traffic" rather than as "this panel is broken" — the same trap as a
    #    silent alert, one artefact over.
    check_dashboards(instruments)

    # 7. The workflow's triggers cover every path this script reads outside its
    #    own tree — asserted on BOTH triggers, because a merged change that
    #    skips the gate on `main` is the same defect one branch later.
    check_workflow_covers_inputs()

    return report()


def check_dashboards(instruments: set[str]) -> None:
    dashboards = sorted((OBSERVABILITY / "dashboards").glob("*.json"))

    if not dashboards:
        fail("found no dashboards in deploy/observability/dashboards")
        return

    for path in dashboards:
        try:
            document = json.loads(read(path))
        except json.JSONDecodeError as error:
            fail(f"{path.name}: not valid JSON — {error}")
            continue

        expressions = [
            target["expr"]
            for panel in document.get("panels", [])
            for target in panel.get("targets", [])
            if "expr" in target
        ]

        # Subject again: a dashboard whose panels this cannot read would pass
        # the loop below by having nothing to check. Row panels carry no
        # targets, so the assertion is per FILE rather than per panel.
        if not expressions:
            fail(f"{path.name}: no panel expressions found — the reader, not the file")
            continue

        for expression in expressions:
            for metric in sorted(metrics_in(expression)):
                if not is_published(metric, instruments):
                    fail(
                        f"{path.name}: a panel reads `{metric}`, which no C# instrument "
                        f"declares and EXTERNAL_METRICS does not list")


def check_workflow_covers_inputs() -> None:
    text = read(WORKFLOW)
    blocks = re.split(r"^\s{2}(push|pull_request):\s*$", text, flags=re.MULTILINE)

    triggers = {}
    for index in range(1, len(blocks) - 1, 2):
        triggers[blocks[index]] = blocks[index + 1]

    for name in ("push", "pull_request"):
        if name not in triggers:
            fail(f"{WORKFLOW.name}: no `{name}` trigger — the gate must run on both")
            continue

        body = triggers[name]
        for entry in SOURCE_INPUTS + ["deploy/observability"]:
            if entry not in body:
                fail(
                    f"{WORKFLOW.name}: the `{name}` trigger does not cover `{entry}`, "
                    f"which check.py reads. A change to it would skip this gate")


def report() -> int:
    if failures:
        print("observability gate: FAILED", file=sys.stderr)
        for message in failures:
            print(f"  - {message}", file=sys.stderr)
        return 1

    print("observability gate: OK")
    return 0


if __name__ == "__main__":
    sys.exit(main())
