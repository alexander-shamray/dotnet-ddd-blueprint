#!/usr/bin/env python3
"""Fetch a deployed realm through Keycloak's admin API and write it out.

The one file in `deploy/keycloak` that talks to anything. It is deliberately
thin and deliberately separate: `realm_check.py` decides, this fetches, and
keeping the network on the other side of a file boundary is what lets the
decision have a suite at all — `deploy/canary`'s split, adopted rather than
re-invented.

**What it writes is a realm export, not a new format.** Keycloak's
`GET /admin/realms/{realm}` answers a `RealmRepresentation` and
`GET /admin/realms/{realm}/clients` answers the `ClientRepresentation` list
that a full export carries under `clients`; joining the two produces the same
document shape `deploy/compose/keycloak/realm-export.json` holds. That is why
one predicate can judge both, and it is the whole reason this file exists in
this form rather than as a second checker.

**It never interprets.** A missing key stays missing and a client this
repository has never heard of is written out unchanged, because the only thing
that may decide whether a realm is compliant is the file that has a suite.

**Credentials fail closed.** Every one of the four environment variables is
required and an absent one stops the run naming it. There is no "check what we
can": a realm-obligation check that quietly degrades to checking nothing is the
fail-open shape §12's gate-coverage rule refuses, and it would report a pass on
the exact realm this gate was filed for.

**Plain HTTP is refused.** The admin API carries a bearer token that can read
every client secret in the realm, and §11.2 already refuses metadata over plain
HTTP outside Development. There is no development affordance here because there
is no local subject: the Compose realm is checked from its file.

Stdlib `urllib`, so `deploy/keycloak` adds no dependency and no
licence-register entry. Two GETs and a form POST is not a client library.

    py -3.12 deploy/keycloak/read_admin.py --out realm.json
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

TIMEOUT_SECONDS = 30

# The environment this reads, and every entry is required. `KEYCLOAK_BASE_URL`
# is the server root and not the realm's issuer URL — the admin endpoints sit
# beside `/realms`, not under it, and handing this an issuer URL is the mistake
# worth naming in the help text.
#
# NAMED ONCE, IN THE TUPLE, AND UNPACKED. The four constants below hold
# variable *names* and never a value, and spelling one as
# `CLIENT_SECRET = "..."` is the shape §15.1's secret scan exists to catch --
# a credential-shaped name assigned a literal. It would have been a false
# positive and an allow-list line, and an accepted finding is a decision
# somebody has to re-read; naming them here instead costs one line and leaves
# that rule meaning exactly what it says.
REQUIRED = (
    "KEYCLOAK_BASE_URL",
    "KEYCLOAK_REALM",
    "KEYCLOAK_CHECK_CLIENT_ID",
    "KEYCLOAK_CHECK_CLIENT_SECRET",
)
BASE_URL, REALM, CLIENT_ID, CLIENT_SECRET = REQUIRED


def environment() -> dict[str, str]:
    """The four required values, or a stop naming every one that is missing.

    Every one, not the first. An operator wiring this into an environment for
    the first time should learn what it needs in one run rather than four.
    """
    missing = [name for name in REQUIRED if not os.environ.get(name, "").strip()]
    if missing:
        raise SystemExit(
            "read_admin: the realm check needs " + ", ".join(missing) +
            ". These are deploy-time credentials for a Keycloak service "
            "account with realm-read rights; docs/secrets.md carries how they "
            "are provisioned and rotated. Refusing to run rather than "
            "reporting a realm nobody read.")

    values = {name: os.environ[name].strip() for name in REQUIRED}
    base = values[BASE_URL].rstrip("/")
    if not base.startswith("https://"):
        raise SystemExit(
            f"read_admin: {BASE_URL} is {base!r}. The admin API carries a "
            "bearer token that can read every client secret in the realm, so "
            "it is https or nothing.")
    values[BASE_URL] = base
    return values


def token(base_url: str, realm: str, client_id: str, client_secret: str) -> str:
    """A client-credentials access token for the service account.

    The token endpoint is the realm's own, so the credential is a client of the
    realm being checked. That is deliberate: a cross-realm admin account would
    hold rights over realms this check has no business reading.
    """
    # `safe=""`, because `quote` leaves `/` alone by default and `/` is the
    # only character that changes which realm this reads. A KEYCLOAK_REALM of
    # `commerce/../master` would otherwise normalise to a different realm and
    # this run would report that one holding section 11's obligations.
    url = (f"{base_url}/realms/{urllib.parse.quote(realm, safe='')}"
           "/protocol/openid-connect/token")
    form = urllib.parse.urlencode({
        "grant_type": "client_credentials",
        "client_id": client_id,
        "client_secret": client_secret,
    }).encode("ascii")

    request = urllib.request.Request(url, data=form, method="POST")
    request.add_header("Content-Type", "application/x-www-form-urlencoded")
    with urllib.request.urlopen(request, timeout=TIMEOUT_SECONDS) as response:  # noqa: S310
        body = json.loads(response.read().decode("utf-8"))

    access = body.get("access_token")
    if not isinstance(access, str) or not access:
        # The body is NOT printed. A failed token response can echo the request
        # form back, and this one carries a client secret.
        raise SystemExit(
            "read_admin: the token endpoint returned no access_token. The "
            "response body is withheld because it can contain the credential "
            "that was sent.")
    return access


def get(url: str, access_token: str) -> object:
    request = urllib.request.Request(url, method="GET")
    request.add_header("Authorization", f"Bearer {access_token}")
    request.add_header("Accept", "application/json")
    with urllib.request.urlopen(request, timeout=TIMEOUT_SECONDS) as response:  # noqa: S310
        return json.loads(response.read().decode("utf-8"))


def fetch(values: dict[str, str]) -> dict:
    """The realm representation with its clients joined in, exactly as an export holds them."""
    base = values[BASE_URL]
    realm = urllib.parse.quote(values[REALM], safe="")
    access = token(base, values[REALM], values[CLIENT_ID], values[CLIENT_SECRET])

    representation = get(f"{base}/admin/realms/{realm}", access)
    if not isinstance(representation, dict):
        raise SystemExit("read_admin: the admin API did not answer a realm representation.")

    clients = get(f"{base}/admin/realms/{realm}/clients", access)
    if not isinstance(clients, list):
        raise SystemExit("read_admin: the admin API did not answer a client list.")

    # The realm representation carries no `clients` key of its own, so this
    # adds rather than overwrites. Asserting that keeps a future Keycloak
    # answering a partial list from being silently replaced by a full one, or
    # the reverse.
    if "clients" in representation:
        raise SystemExit(
            "read_admin: the realm representation already carries a clients "
            "key. Joining the client list would overwrite it, and which of the "
            "two the checker should judge is a decision this file must not "
            "take on its own.")
    representation["clients"] = clients
    return representation


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--out", required=True, type=Path,
                        help="where to write the realm document realm_check.py will read")
    args = parser.parse_args(argv[1:])

    values = environment()
    try:
        realm = fetch(values)
    except (OSError, json.JSONDecodeError, UnicodeDecodeError) as error:
        # A realm that cannot be read is not a realm that passed. This is the
        # canary's rule for an unreachable Prometheus, one artefact over.
        #
        # WIDER THAN `URLError`, and the width is the point. `urllib` wraps
        # only connection-phase failures; a reset or a timeout during
        # `response.read()` raises the bare `OSError`, and a proxy answering a
        # 200 with an HTML error page raises out of `json.loads`. Both are the
        # commonest way this step fails and both used to escape as a
        # traceback — loud, and wrong about what happened. `URLError` is an
        # `OSError`, so this still covers it.
        #
        # THE ERROR IS PRINTED AND THE VALUES ARE NOT. Only the base URL is
        # named, on `token`'s rule: a failed exchange can echo the request form
        # back, and that form carries the client secret.
        raise SystemExit(
            f"read_admin: {values[BASE_URL]} could not be read: "
            f"{type(error).__name__}: {error}. A rollout that cannot see its "
            "own realm has to stop rather than guess.") from error

    args.out.write_text(json.dumps(realm, indent=2), encoding="utf-8")
    print(f"read_admin: wrote {values[REALM]} with "
          f"{len(realm.get('clients', []))} client(s) to {args.out}.")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
