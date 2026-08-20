#!/usr/bin/env python3
"""Run the plan's queries against Prometheus and write what came back.

The one file in `deploy/canary` that talks to anything. It is deliberately thin
and deliberately separate: `canary.py` decides, this fetches, and keeping the
network on the other side of a file boundary is what lets the decision have a
suite at all.

**It never interprets.** A query that matched no series produces `null` here,
not a zero -- and `canary.py analyse` reads `null` as a rollback, because an
absent series and a healthy one look identical from a dashboard and §13.6
spends a callout on exactly that. Substituting a zero would turn "nobody
scraped the canary" into "the canary had no errors", which promotes on a
measurement that did not happen.

Stdlib `urllib`, so `deploy/canary` adds no dependency and no licence-register
entry. Prometheus' HTTP API is one GET and a JSON body; a client library would
be a package to pin for a call this short.

    py -3.12 deploy/canary/read_prometheus.py --service Catalog.Api --window 10m --out readings.json
"""

from __future__ import annotations

import argparse
import json
import os
import sys
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path

from canary import entries, load_plan

TIMEOUT_SECONDS = 30


def query(base_url: str, expression: str) -> float | None:
    """One instant query, or None when there is nothing to read.

    None covers three different silences on purpose: an empty result, a
    non-finite value, and a body that does not parse. All three mean the same
    thing to the caller -- this metric was not measured -- and distinguishing
    them here would invite a caller to treat one of them as a reading.

    A transport or HTTP error is NOT one of them. That is the monitoring stack
    being unreachable rather than the canary being unobserved, and a rollout
    that cannot see its own monitoring must stop rather than guess; it raises.
    """
    url = f"{base_url.rstrip('/')}/api/v1/query?" + urllib.parse.urlencode({"query": expression})
    with urllib.request.urlopen(url, timeout=TIMEOUT_SECONDS) as response:  # noqa: S310
        body = json.loads(response.read().decode("utf-8"))

    if body.get("status") != "success":
        raise RuntimeError(f"Prometheus refused the query: {body.get('error', body)}")

    result = body.get("data", {}).get("result", [])
    if not result:
        return None

    value = float(result[0]["value"][1])
    # NaN is what `histogram_quantile` returns for a histogram with no
    # observations, and it compares false against every threshold -- so a NaN
    # read as a number would sail through both the absolute and the relative
    # checks and promote.
    return None if value != value else value


def read(base_url: str, service: str, window: str, plan: dict) -> dict:
    readings: dict[str, dict[str, float | None]] = {}
    for track in ("canary", "baseline"):
        # `baseline` is this run's name for the stable track, and `stable` is
        # the label's. They are kept distinct because the plan's vocabulary is
        # about the comparison and the cluster's is about the deployment.
        label = "stable" if track == "baseline" else "canary"
        readings[track] = {
            name: query(
                base_url,
                expression
                .replace("$SERVICE", service)
                .replace("$TRACK", label)
                .replace("$WINDOW", window),
            )
            for name, expression in entries(plan["queries"]).items()
        }
    return readings


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--service", required=True, help="the service_name resource attribute")
    parser.add_argument("--window", required=True, help="the step's dwell, as a PromQL duration")
    parser.add_argument("--out", required=True, type=Path)
    args = parser.parse_args(argv[1:])

    base_url = os.environ.get("PROMETHEUS_URL")
    if not base_url:
        # Blank counts as missing, which is a lesson this repository learned
        # against an environment variable three times.
        print("read_prometheus: PROMETHEUS_URL is unset", file=sys.stderr)
        return 1

    try:
        readings = read(base_url, args.service, args.window, load_plan())
    except (urllib.error.URLError, RuntimeError, OSError) as error:
        print(f"read_prometheus: {error}", file=sys.stderr)
        return 1

    args.out.write_text(json.dumps(readings, indent=2), encoding="utf-8")
    print(json.dumps(readings, indent=2))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
