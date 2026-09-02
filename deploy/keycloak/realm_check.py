#!/usr/bin/env python3
"""Every realm this platform is pointed at holds §11's token obligations.

[§11.3](../../docs/backend-architecture/11-identity-authorization.md) states a
300-second access-token lifetime and
[ADR-033](../../docs/backend-architecture/appendix-a-adrs.md) composes the
330-second revocation bound out of it; ADR-034 states that the browser is
issued no refresh token. Both are realm settings, and until this gate existed
the only realm anything read was `deploy/compose/keycloak/realm-export.json` —
§14.1's Compose realm. Every chart points at
`https://id.example.com/realms/commerce`, so a deployed realm could issue
five-hour access tokens, or hand the browser a refresh token, while every
sentence in §11.2, §11.3, ADR-033 and ADR-034 still read as platform guarantees
and the suite stayed green
([#157](https://github.com/alexander-shamray/dotnet-ddd-blueprint/issues/157)).

**One predicate, two subjects, and the second one is the point.** A Keycloak
realm export and the admin API's `RealmRepresentation` are the same document —
the export is that representation serialised — so the obligations below can be
asserted against a file in CI and against a live realm at deploy time by one
piece of code. `read_admin.py` fetches; this file decides; neither knows which
realm it was handed.

**The realm kind is an argument because one obligation inverts on it.**
`directAccessGrantsEnabled` is `true` in the Compose realm — §11.2's password
grant, the affordance that lets `docs/` document a `curl` login — and §11.2
says outright that a deployed realm turns it off. So `RealmImportTests` asserts
that flag *true* and this gate asserts it *false* for a deployed realm, and
those two are only coherent if the kind is named rather than defaulted. It has
no default for that reason: a check that guesses which realm it is looking at
would pass a production realm on the local realm's terms.

**The lifetime is read, not restated.** `AuthenticationExtensions` declares
`AccessTokenLifetime` and ADR-040 made it the one place the 300 is written;
a literal here would be a second statement that agrees until one of them is
edited. Reading it also means this gate fails when that file is restructured,
which is the honest outcome — it can no longer say what the realm owes.

Stdlib only, on the licence gate's terms: no restore, no dependencies, and it
runs before anything is built.

    py -3.12 deploy/keycloak/realm_check.py check --realm deploy/compose/keycloak/realm-export.json --kind local
    py -3.12 deploy/keycloak/realm_check.py inputs
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]

LIFETIME_SOURCE = "src/BuildingBlocks/Common.Web/AuthenticationExtensions.cs"

# The default subject, and the only realm this repository owns. Naming it as a
# constant rather than in CI's argv is what puts it inside the self-check
# below: a path this file never spells is a path the reads-direction check
# cannot see. The deploy path always passes `--realm` explicitly, because the
# realm it checks is one nothing here holds a copy of.
COMPOSE_REALM = "deploy/compose/keycloak/realm-export.json"

# What this gate reads outside its own tree, declared beside the reads rather
# than left to be discovered — the observability gate's invention, and
# `check_source_inputs_covers_reads` below is what keeps this list honest.
#
# BUILT FROM THE CONSTANTS ABOVE AND NOT RESTATED. That is a decision about
# what the self-check is for: a list spelling those two paths a second time
# would make the reads direction agree with itself, which is a check whose
# subject is its own copy. What it looks for instead is a path literal
# anywhere in this file that no entry covers, so the read this gate grows next
# is the one it catches.
SOURCE_INPUTS = [LIFETIME_SOURCE, COMPOSE_REALM]

WORKFLOW_PATH = ".github/workflows/realm.yml"

# This gate's own tree, subtracted from the reads direction. Its own path
# appears in the docstring's invocation lines, and a gate that demanded a
# SOURCE_INPUTS entry covering itself would be asking the workflow to name it
# twice — `check_workflow_covers_inputs` already adds it to what both triggers
# must cover.
OWN_TREE = "deploy/keycloak"

# A quoted path with at least one separator, and the repository root joined
# with a module constant. `check_source_inputs_covers_reads` needs both; the
# argument for two scans is there rather than here.
PATH_LITERAL = r'"((?:[A-Za-z0-9_.-]+/)+[A-Za-z0-9_.-]+)"'
ROOT_USE = r"(?:^|[^A-Za-z0-9_.])(?:ROOT|root)\s*/\s*([A-Z_][A-Z0-9_]*)"

# The two triggers `realm.yml` must carry, named so a failure can say which.
TRIGGERS = ("pull_request", "push")

# `web-app` is §11.2's browser client, and naming it here is a restatement of a
# name the chapter fixes and `RealmImportTests` also spells. That is admitted
# rather than avoided: the alternative is inferring which client is the browser
# from its flags, and every flag that would identify it is one of the settings
# below — a subject derived from the predicate passes vacuously the moment the
# predicate is what has gone wrong.
BROWSER_CLIENT = "web-app"

LOCAL = "local"
DEPLOYED = "deployed"
KINDS = (LOCAL, DEPLOYED)

# Every flag this gate reads, and it reads no other. Keycloak serialises these
# as JSON booleans in both an export and an admin-API answer, so anything else
# is a hand-edited realm — and `check_flags_are_booleans` refuses one rather
# than comparing it. The comparisons below are all identity tests against
# `True` or `False`, which means a string `"true"` would be neither enabled nor
# disabled but *unjudged*, and an unjudged flag is a pass.
FLAGS = (
    "implicitFlowEnabled",
    "standardFlowEnabled",
    "directAccessGrantsEnabled",
)


def read_access_token_lifetime(root: Path = ROOT) -> int:
    """The 300, taken out of `AuthenticationExtensions` rather than written here.

    Raises rather than defaulting. A gate that cannot find the number it is
    checking against has to say so — substituting 300 would make the read
    decorative, which is the shape ADR-033 was written to withdraw.
    """
    source = root / LIFETIME_SOURCE
    try:
        text = source.read_text(encoding="utf-8")
    except OSError as error:
        raise SystemExit(f"realm-gate: {LIFETIME_SOURCE} is not readable: {error}") from error

    # COMMENTS FIRST, AND THE MEMBER IS ANCHORED. `AuthenticationExtensions`
    # carries doc comments that name `AccessTokenLifetime`, so a prose mention
    # of the assignment plus a reformatted declaration would leave exactly one
    # match -- in the comment -- and this gate would assert a number the
    # platform no longer holds. Stripping the comment lines and requiring
    # `readonly TimeSpan <name> =` makes both halves of that unlikely pair
    # impossible rather than improbable.
    code = "\n".join(
        line for line in text.splitlines() if not line.lstrip().startswith("//"))
    matches = re.findall(
        r"readonly\s+TimeSpan\s+AccessTokenLifetime\s*=\s*"
        r"TimeSpan\.FromSeconds\(\s*(\d+)\s*\)", code)
    if len(matches) != 1:
        raise SystemExit(
            f"realm-gate: {LIFETIME_SOURCE} declares AccessTokenLifetime "
            f"{len(matches)} time(s), expected exactly one. The lifetime a realm "
            "owes is read from that declaration, so this gate cannot say what "
            "the realm owes and must not report a pass.")
    return int(matches[0])


def clients_of(realm: dict) -> list[dict]:
    """The realm's clients, or an empty list when the key is absent or wrong.

    Absent and empty are the same answer to every caller here — there is
    nothing to judge — and `check_realm` refuses that answer outright.
    """
    clients = realm.get("clients")
    return clients if isinstance(clients, list) else []


def check_realm(realm: dict, kind: str, lifetime: int) -> list[str]:
    """The obligations of §11.3, ADR-033 and ADR-034, against one realm document.

    Every check that follows names a client or a realm key, so the first thing
    established is that there are clients to name. A realm document with no
    `clients` array satisfies "no client overrides the lifetime" and "no client
    enables the implicit flow" perfectly, and answering that with a pass is the
    vacuous-gate failure this repository repeats most.
    """
    if kind not in KINDS:
        return [f"the realm kind {kind!r} is not one of {', '.join(KINDS)}"]

    problems: list[str] = []
    clients = clients_of(realm)
    if not clients:
        return [
            "the realm document carries no clients array, so every per-client "
            "obligation below would pass without judging anything. This is a "
            "malformed or truncated realm, not a compliant one"
        ]

    named = [c for c in clients if isinstance(c, dict) and c.get("clientId") == BROWSER_CLIENT]
    if len(named) != 1:
        problems.append(
            f"the realm declares the browser client {BROWSER_CLIENT!r} "
            f"{len(named)} time(s), expected exactly one. ADR-034's "
            "refresh-token obligation is a property of that client and cannot "
            "be checked without it")

    problems += check_flags_are_booleans(clients)
    problems += check_lifetime(realm, clients, lifetime)
    problems += check_implicit_flow(clients)
    if named:
        problems += check_browser_client(named[0], kind)
    return problems


def check_flags_are_booleans(clients: list[dict]) -> list[str]:
    """A flag that is not a boolean is refused rather than compared.

    Absent is allowed here and judged where it matters — an absent flag is
    Keycloak's default and every check below decides for itself whether that
    default satisfies the obligation. What this refuses is a *present* value of
    the wrong type, because every comparison in this file is an identity test:
    `"true"` is neither `True` nor `False`, so it would fall through
    `check_implicit_flow` as though the flow were off.
    """
    problems: list[str] = []
    for client in clients:
        if not isinstance(client, dict):
            problems.append(
                f"the clients array holds a {type(client).__name__} where a "
                "client object belongs, so every obligation below would skip it")
            continue
        for flag in FLAGS:
            # `null` is NOT a wrong type here, it is an unstated one — and
            # what an unstated flag means differs per obligation, so it is
            # left to the check that reads it rather than decided in advance.
            if flag in client and client[flag] is not None and not isinstance(client[flag], bool):
                problems.append(
                    f"client {client.get('clientId')!r} sets {flag}="
                    f"{client[flag]!r}, which is not a boolean. Every check "
                    "here compares against true or false, so a value of any "
                    "other type would be neither and would pass unjudged")
    return problems


def check_lifetime(realm: dict, clients: list[dict], lifetime: int) -> list[str]:
    """The realm's lifetime is the chapter's, and no client overrides it.

    Two settings, because Keycloak resolves the client attribute over the realm
    value: a realm at 300 with one client at 18000 issues five-hour tokens to
    that client, and the realm-level assertion alone would call it compliant.
    """
    problems: list[str] = []
    declared = realm.get("accessTokenLifespan")
    if declared != lifetime:
        problems.append(
            f"accessTokenLifespan is {declared!r}, and {LIFETIME_SOURCE} "
            f"declares {lifetime}. ADR-033's revocation bound is that number "
            "plus the 30-second ClockSkew, so a realm that disagrees widens a "
            "window no chapter re-states")

    for client in clients:
        if not isinstance(client, dict):
            continue
        attributes = client.get("attributes")
        if not isinstance(attributes, dict):
            continue
        override = attributes.get("access.token.lifespan")
        if override is None or not str(override).strip():
            # A BLANK STRING IS NOT AN OVERRIDE, and reading it as one would
            # fail a compliant realm. Keycloak stores "" for an advanced
            # setting that was filled in and then cleared in the console, which
            # is the ordinary way an operator undoes exactly the mistake this
            # check exists to catch -- so the shape produced by the fix would
            # have failed the rollout.
            continue

        seconds = str(override).strip()
        if not seconds.lstrip("-").isdigit():
            problems.append(
                f"client {client.get('clientId')!r} sets "
                f"access.token.lifespan={override!r}, which is not a number of "
                "seconds. This gate cannot say what lifetime that client "
                "issues, which is not the same as saying it is the realm's")
        # An override equal to the realm value is not a finding. It is
        # redundant rather than wrong, and failing it would make this gate
        # refuse a realm that holds the obligation it exists to enforce.
        elif int(seconds) != lifetime:
            problems.append(
                f"client {client.get('clientId')!r} sets "
                f"access.token.lifespan={override!r}, overriding the realm's "
                f"{lifetime}. A client-level lifespan is the misconfiguration "
                "this gate was filed for")
    return problems


def check_implicit_flow(clients: list[dict]) -> list[str]:
    """No client enables the implicit flow, which is what makes the other lifespan moot.

    `accessTokenLifespanForImplicitFlow` is 900 in the shipped realm and is not
    asserted anywhere, because nothing can reach it. That is only true while no
    client enables the flow, so this is the check that keeps the silence about
    the other setting honest rather than a gap.
    """
    problems: list[str] = []
    for client in clients:
        if isinstance(client, dict) and client.get("implicitFlowEnabled") is True:
            problems.append(
                f"client {client.get('clientId')!r} enables the implicit flow. "
                "accessTokenLifespanForImplicitFlow then governs its tokens, "
                "and no chapter states a value for it")
    return problems


def check_browser_client(client: dict, kind: str) -> list[str]:
    """ADR-034's refresh-token rule, and §11.2's password grant.

    The refresh-token attribute is checked for presence and not only for value.
    Keycloak's default is to issue refresh tokens on the standard flow, so an
    absent `use.refresh.tokens` is the violation spelled as a silence — reading
    a missing attribute as compliant would make the one setting ADR-034 rests
    on optional.
    """
    problems: list[str] = []
    attributes = client.get("attributes")
    attributes = attributes if isinstance(attributes, dict) else {}

    refresh = attributes.get("use.refresh.tokens")
    if refresh is None:
        problems.append(
            f"client {BROWSER_CLIENT!r} declares no use.refresh.tokens "
            "attribute. Keycloak issues refresh tokens on the standard flow by "
            "default, so the absence is ADR-034 violated and not unspecified")
    elif str(refresh).lower() != "false":
        problems.append(
            f"client {BROWSER_CLIENT!r} sets use.refresh.tokens={refresh!r}. "
            "ADR-034 gives the browser an access token and no refresh token")

    # The positive half. Without it the attribute above holds for the wrong
    # reason: a client with no standard flow issues no refresh token because it
    # issues nothing at all, and the check would pass on a broken realm.
    if client.get("standardFlowEnabled") is not True:
        problems.append(
            f"client {BROWSER_CLIENT!r} does not enable the standard flow. "
            "The refresh-token obligation above then holds because the client "
            "mints no token at all, which is not the guarantee ADR-034 states")

    grants = client.get("directAccessGrantsEnabled")
    if kind == DEPLOYED and grants is not False:
        problems.append(
            f"client {BROWSER_CLIENT!r} has directAccessGrantsEnabled="
            f"{grants!r}. Section 11.2 documents the password grant as a local "
            "affordance and says a deployed realm turns it off")
    if kind == LOCAL and grants is not True:
        problems.append(
            f"client {BROWSER_CLIENT!r} has directAccessGrantsEnabled="
            f"{grants!r}. Section 14.1's documented login is a password grant, "
            "so the local realm needs it and the README's curl would not work")
    return problems


def check_source_inputs_covers_reads() -> list[str]:
    """Every path this file reads is covered by a SOURCE_INPUTS entry.

    Grepping its own source, because the list and the reads drift the moment a
    check grows a second input. `SOURCE_INPUTS` is the Helm tree's, and this
    direction is what every copy of it was found to owe after `canary.py`
    declared two paths and opened three — adopted here rather than re-learned,
    and `docs/lessons.md` carries the measurement.

    **This direction is not the other one.** It establishes that nothing is
    read undeclared; it says nothing about a declared entry no constant spells,
    and it must not — the workflow-trigger direction below is where a declared
    entry earns its keep, and an entry read only through argv is still a change
    that has to run this gate.

    `WORKFLOW_PATH` is subtracted rather than matched. This file does read it,
    and it is deliberately not a SOURCE_INPUTS entry: the trigger check adds it
    to what the workflow must cover, so declaring it here would make the
    workflow require itself twice and say nothing new.
    """
    source = Path(__file__).resolve().read_text(encoding="utf-8")

    # TWO SCANS, BECAUSE ONE OF THEM CANNOT SEE A FILE AT THE REPOSITORY ROOT.
    # The literal scan requires a separator, so a read of `global.json` or
    # `Platform.slnx` would match nothing and this check would report a pass on
    # the very omission it exists to catch. Dropping the separator is not the
    # fix: `access.token.lifespan` is a dotted bare word too, and this file is
    # full of them. So the second scan looks at how a path is *used*: joining
    # the repository root with a constant names a read, whatever it looks like.
    # COMMENT LINES ARE NOT CODE, and this scan reads its own source. Without
    # the strip, the sentence above describing the pattern satisfies it, and
    # the check reports a read of a path nothing opens — a self-check that
    # fails on its own documentation is one somebody deletes.
    code = "\n".join(
        line for line in source.splitlines() if not line.lstrip().startswith("#"))

    quoted = set(re.findall(PATH_LITERAL, source))
    # A dot is an ordinary character in a path segment, because `Common.Web` is
    # a directory. An earlier form allowed one only at the start of a segment,
    # which quietly matched nothing in the one read this gate most depends on —
    # found by the test below rather than by reading it, which is the whole
    # argument for having that test. `..` is subtracted as the price: the
    # docstring's relative links to the blueprint are not reads.
    quoted = {r for r in quoted if ".." not in r.split("/")}

    problems: list[str] = []
    used = set()
    for name in re.findall(ROOT_USE, code):
        value = globals().get(name)
        if isinstance(value, str):
            used.add(value)
        else:
            problems.append(
                f"this gate reads ROOT / {name}, which is not a module-level "
                "string constant, so the reads-direction check cannot say what "
                "path it is")

    reads = {r for r in quoted | used
             if r != WORKFLOW_PATH and r != OWN_TREE and not r.startswith(f"{OWN_TREE}/")}
    if not reads:
        return problems + [
            "the self-check found no path literal and no root-joined constant "
            "in this file, so it is the scan that is broken rather than the "
            "list that is complete"
        ]

    for read in sorted(reads):
        if not any(read == entry or read.startswith(f"{entry}/") for entry in SOURCE_INPUTS):
            problems.append(
                f"{read} is read by this gate and covered by no SOURCE_INPUTS "
                f"entry, so {WORKFLOW_PATH} will not run on a change to it")
    return problems


def check_workflow_covers_inputs(root: Path = ROOT) -> list[str]:
    """Both of the workflow's triggers cover every declared input.

    **Both, and each one named.** A merged change that skips the gate on `main`
    is the same defect one branch later — and an earlier form of this check
    counted two `paths:` blocks without asking which events they belonged to,
    so replacing `push` with any other trigger that accepts `paths` left it
    green while nothing ran the gate on `main` at all. It now anchors each
    block to its event, which is also what lets a failure name the event
    instead of a position.

    It reads text rather than YAML, on the licence gate's terms: stdlib has no
    parser and a gate needing a `pip install` is a gate that gets skipped. The
    cost is that only the quoting styles below are recognised, and the cost is
    paid as a **refusal** rather than a pass — an unrecognised list reports
    that this check cannot say whether the inputs are covered.
    """
    workflow = root / WORKFLOW_PATH
    try:
        text = workflow.read_text(encoding="utf-8")
    except OSError as error:
        return [f"{WORKFLOW_PATH} is not readable: {error}"]

    problems = []
    for event in TRIGGERS:
        block = re.search(
            rf"^  {event}:\s*\n(?:(?!^  \S).)*?^    paths:\s*\n((?:^ *-[^\n]*\n)+)",
            text, re.MULTILINE | re.DOTALL)
        if block is None:
            problems.append(
                f"{WORKFLOW_PATH} has no {event} trigger with a paths list this "
                "check can read. That is not the same as saying its inputs are "
                "covered, so it is reported rather than skipped")
            continue

        patterns = re.findall(r"-\s*['\"]?([^'\"\s#]+)['\"]?", block.group(1))
        for entry in SOURCE_INPUTS + [OWN_TREE, WORKFLOW_PATH]:
            if not any(p == entry or p == f"{entry}/**" for p in patterns):
                problems.append(
                    f"{WORKFLOW_PATH}'s {event} trigger does not cover {entry}, "
                    "so a change to it would not run this gate")
    return problems


def load_realm(path: Path) -> dict:
    try:
        document = json.loads(path.read_text(encoding="utf-8"))
    except OSError as error:
        raise SystemExit(f"realm-gate: {path} is not readable: {error}") from error
    except json.JSONDecodeError as error:
        raise SystemExit(f"realm-gate: {path} is not JSON: {error}") from error
    if not isinstance(document, dict):
        raise SystemExit(f"realm-gate: {path} is not a realm representation")
    return document


def fail(problems: list[str], subject: str) -> int:
    if not problems:
        return 0
    print(f"realm-gate: {len(problems)} problem(s) with {subject}:\n", file=sys.stderr)
    for problem in problems:
        print(f"  - {problem}", file=sys.stderr)
    return 1


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description="Check a Keycloak realm against section 11's obligations.")
    commands = parser.add_subparsers(dest="command", required=True)

    check = commands.add_parser("check", help="one realm document against the obligations")
    check.add_argument("--realm", type=Path, default=ROOT / COMPOSE_REALM,
                       help="a Keycloak realm export, or the file read_admin.py wrote; "
                            f"defaults to {COMPOSE_REALM}, the one realm this repository owns")
    check.add_argument("--kind", required=True, choices=KINDS,
                       help="which realm this is; it has no default because one obligation inverts on it")

    commands.add_parser("inputs", help="this gate's reads against its workflow's triggers")

    args = parser.parse_args(argv[1:])

    if args.command == "inputs":
        problems = check_source_inputs_covers_reads() + check_workflow_covers_inputs()
        if code := fail(problems, "this gate's declared inputs"):
            return code
        print(f"realm-gate: {len(SOURCE_INPUTS)} declared input(s), all read and all triggered.")
        return 0

    lifetime = read_access_token_lifetime()
    realm = load_realm(args.realm)
    problems = check_realm(realm, args.kind, lifetime)
    if code := fail(problems, f"the {args.kind} realm in {args.realm}"):
        return code
    print(f"realm-gate: the {args.kind} realm in {args.realm} holds all "
          f"{len(clients_of(realm))} client(s) to a {lifetime}-second lifetime "
          "and the browser to no refresh token.")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
