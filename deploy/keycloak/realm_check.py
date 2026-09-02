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
# `deploy/helm` is read by the SUITE rather than by the gate, and it is
# declared all the same. `test_the_shipped_charts_carry_an_authority_this_can
# _split` reads every chart's `identity.authority` to establish that the value
# the deploy path splits still has the shape it splits — a subject test that
# does not gate the files it validates is a subject test in name only.
CHART_VALUES = "deploy/helm"

SOURCE_INPUTS = [LIFETIME_SOURCE, COMPOSE_REALM, CHART_VALUES]

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
# `root / CONSTANT` and `root / module.CONSTANT` alike. The qualified form is
# how a SIBLING module names one of this file's paths — which is the shape the
# suite uses, and which the unqualified pattern could not see: the entry it
# needs was passing on the strength of its own declaration rather than on a
# read anything had found.
ROOT_USE = r"(?:^|[^A-Za-z0-9_.])(?:ROOT|root)\s*/\s*(?:[a-z_][A-Za-z0-9_]*\.)?([A-Z_][A-Z0-9_]*)"

# The two triggers `realm.yml` must carry, named so a failure can say which.
TRIGGERS = ("pull_request", "push")

# `web-app` is §11.2's browser client, and naming it here is a restatement of a
# name the chapter fixes and `RealmImportTests` also spells. That is admitted
# rather than avoided: the alternative is inferring which client is the browser
# from its flags, and every flag that would identify it is one of the settings
# below — a subject derived from the predicate passes vacuously the moment the
# predicate is what has gone wrong.
BROWSER_CLIENT = "web-app"

# A REALM REPRESENTATION CARRIES EVERY CONFIDENTIAL CLIENT'S SECRET, and this
# gate needs none of them. The admin API answers `secret` for each such client
# to a caller with realm-read rights, §14.1's own export carries one, and this
# file's business is flags, lifespans and attribute values — so the credential
# is removed at the door rather than avoided by every message downstream.
#
# The alternative was to police the *messages*, which is the shape that fails:
# nothing stops the next check from formatting a client dict, and CodeQL was
# right to read a document holding a `secret` key flowing into a print as a
# clear-text log. Redacting at load makes the property structural — there is no
# secret in the object at all — rather than a rule every future author must
# remember.
CREDENTIAL_KEYS = frozenset({
    "secret", "password", "value", "privateKey", "publicKey", "certificate",
    "bindCredential", "clientSecret", "adminPassword",
})
REDACTED = "<redacted by realm_check>"

LOCAL = "local"
DEPLOYED = "deployed"
KINDS = (LOCAL, DEPLOYED)

# The chart value that decides which realm every host validates against, and
# the two environment variables `read_admin.py` reads. `authority` derives the
# second pair from the first so the two cannot name different realms: a
# rollout that checked realm A and installed realm B would pass this gate and
# leave the workload pointed at the realm it was filed about.
AUTHORITY_VALUE = ("identity", "authority")
REALMS_SEGMENT = "/realms/"

# The environment `read_admin.py` reads, declared HERE and imported there —
# `deploy/canary`'s direction, where `read_prometheus.py` imports from
# `canary.py` and never the reverse. Two of these four are what `authority`
# writes, so the names have to be one statement or the writer and the reader
# drift; and a name spelled in the fetcher and again in the writer is that
# drift with a file boundary through the middle of it.
#
# Named in a tuple and unpacked, on read_admin.py's own reasoning: a
# credential-shaped constant assigned a literal is the shape §15.1's secret
# scan exists to catch, and these hold variable names and never values.
ENVIRONMENT = (
    "KEYCLOAK_BASE_URL",
    "KEYCLOAK_REALM",
    "KEYCLOAK_CHECK_CLIENT_ID",
    "KEYCLOAK_CHECK_CLIENT_SECRET",
)
BASE_URL_VARIABLE, REALM_VARIABLE = ENVIRONMENT[0], ENVIRONMENT[1]

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


def redact(node: object) -> object:
    """The same document with every credential-shaped value replaced.

    Recursive over objects and arrays, because a realm nests them — a client's
    `secret`, a user's `credentials[].value`, an LDAP component's
    `bindCredential`. The KEY decides, not the value, so nothing here has to
    guess what a secret looks like.

    `value` is in the set and it is the one that costs something: it is a
    generic name, and any `value` key anywhere in a realm is redacted whether
    it held a credential or not. That is the right way round for a document
    this gate only reads flags out of, and it is stated rather than left to be
    discovered by a reader wondering where a field went.
    """
    if isinstance(node, dict):
        return {key: (REDACTED if key in CREDENTIAL_KEYS else redact(value))
                for key, value in node.items()}
    if isinstance(node, list):
        return [redact(item) for item in node]
    return node


def authority_of(values: dict) -> str:
    """`identity.authority` out of a release's own values, or a stop.

    The subject is `helm get values <release> -o json`, which answers what the
    running release was installed with — and what `-f stable-values.yaml`
    reinstalls two steps later. So the realm this gate reads is the realm the
    rollout is about to point every host at, by construction rather than by two
    variables somebody keeps in step.
    """
    node: object = values
    for key in AUTHORITY_VALUE:
        if not isinstance(node, dict) or key not in node:
            path = ".".join(AUTHORITY_VALUE)
            raise SystemExit(
                f"realm-gate: the release's values carry no {path}. Every chart "
                "requires it (§15.4), so a release without one is a release "
                "this gate cannot check rather than one that passes.")
        node = node[key]

    if not isinstance(node, str) or not node.strip():
        raise SystemExit(
            f"realm-gate: identity.authority is {node!r}, which names no realm.")
    return node.strip()


def split_authority(authority: str) -> tuple[str, str]:
    """An OIDC authority into the server root and the realm name.

    Keycloak's admin endpoints sit *beside* `/realms` rather than under it, so
    the two halves are what `read_admin.py` needs and neither is the authority.

    It refuses rather than guesses in four directions, and each one is a realm
    this gate would otherwise read wrongly: plain HTTP, because the token it
    obtains can read every client secret in the realm; a URL carrying a query
    or a fragment, because an admin path appended to one lands inside it; an
    authority with no `/realms/<name>` segment, because Keycloak's admin API is
    not reachable from a root this gate cannot locate; and a realm name with a
    further `/` in it, which is the traversal `read_admin.py` escapes and this
    refuses outright.
    """
    # WHITESPACE ANYWHERE IS REFUSED, AND A NEWLINE IS THE REASON. What this
    # function's two results become is `NAME=value` lines appended to
    # `$GITHUB_ENV`, so a newline inside the value ends that assignment and
    # starts another — an `identity.authority` of
    # `https://host/realms/x\nSOME_VAR=...` sets SOME_VAR for every remaining
    # step of the rollout. The value comes from `helm get values`, so the
    # authority to trust is whatever is in the cluster, and "whoever can edit a
    # release can set this job's environment" is not a privilege this check may
    # hand out. Refusing all whitespace rather than newlines alone: a realm
    # name with a space in it is not one this gate can put in a URL either.
    if any(character.isspace() for character in authority):
        raise SystemExit(
            f"realm-gate: identity.authority is {authority!r}, which contains "
            "whitespace. This value becomes a NAME=value line in $GITHUB_ENV, "
            "where a newline starts a second assignment.")

    if not authority.startswith("https://"):
        raise SystemExit(
            f"realm-gate: identity.authority is {authority!r}. The admin API "
            "carries a bearer token that can read every client secret in the "
            "realm, so it is https or nothing.")
    if "?" in authority or "#" in authority:
        raise SystemExit(
            f"realm-gate: identity.authority is {authority!r}. An admin path "
            "appended to a URL carrying a query or a fragment lands inside it.")

    root, separator, realm = authority.rstrip("/").partition(REALMS_SEGMENT)
    if not separator or not realm:
        raise SystemExit(
            f"realm-gate: identity.authority is {authority!r}, which has no "
            f"{REALMS_SEGMENT}<name> segment. Keycloak's admin endpoints sit "
            "beside that segment, so this gate cannot say where to read.")
    if "/" in realm:
        raise SystemExit(
            f"realm-gate: the realm in {authority!r} is {realm!r}, which is not "
            "a single path segment.")
    return root, realm


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


def code_of(source: str) -> str:
    """The source with its comments and its string paragraphs removed.

    **Three times now, a matcher in this tree has matched the prose about
    the matcher.** A `#` line describing the pattern satisfied it, and a
    docstring saying an earlier form could not see a root-joined name was
    then read as a read of one. Rewording is a fix that lasts until the
    next sentence, so the scans are given code rather than a file.

    Two shapes, and the second was missed on the first attempt. A span
    that OPENS AND CLOSES ON ONE LINE leaves an even delimiter count, so a
    toggle alone keeps it — and a one-line docstring holds prose exactly
    as a paragraph does. Inline spans are removed first, which leaves any
    remaining delimiter as a genuine opener or closer for the toggle.

    A regex rather than an `ast` walk, deliberately: what has to disappear
    is every string written as prose, and `ast` would find only the ones in
    a docstring position.
    """
    inline = re.compile(r'"""[\s\S]*?"""|\'\'\'[\s\S]*?\'\'\'')
    kept: list[str] = []
    inside = False
    for raw in source.splitlines():
        line = raw if inside else inline.sub("", raw)
        delimiters = line.count('"""') + line.count("'''")
        if inside:
            if delimiters % 2:
                inside = False
            continue
        if delimiters % 2:
            inside = True
            continue
        if line.lstrip().startswith("#"):
            continue
        kept.append(line)
    return "\n".join(kept)


def check_source_inputs_covers_reads() -> list[str]:
    """Every path this TREE reads is covered by a SOURCE_INPUTS entry.

    The tree and not this file: what the workflow triggers on is a change to
    anything `deploy/keycloak` reads, and the suite reads a chart value this
    module never opens. Grepping the sources, because the list and the reads
    drift the moment a check grows a second input. `SOURCE_INPUTS` is the Helm tree's, and this
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
    # THE WHOLE TREE, NOT THIS FILE. What the workflow triggers on is a change
    # to anything `deploy/keycloak` reads, and the suite reads a chart value
    # this file never opens — a declared-inputs list scoped to one module is a
    # list that goes stale the first time a sibling grows a read.
    source = "\n".join(
        module.read_text(encoding="utf-8")
        for module in sorted(Path(__file__).resolve().parent.glob("*.py")))

    # TWO SCANS, BECAUSE ONE OF THEM CANNOT SEE A FILE AT THE REPOSITORY ROOT.
    # The literal scan requires a separator, so a read of `global.json` or
    # `Platform.slnx` would match nothing and this check would report a pass on
    # the very omission it exists to catch. Dropping the separator is not the
    # fix: `access.token.lifespan` is a dotted bare word too, and this file is
    # full of them. So the second scan looks at how a path is *used*: joining
    # the repository root with a constant names a read, whatever it looks like.
    # PROSE IS NOT CODE, and both scans read the stripped copy. Only one of
    # them did in the first draft, and the strip covered only `#` lines in
    # the second -- each time the documentation of a pattern satisfied it,
    # and a self-check that fails on its own explanation is one somebody
    # deletes.
    code = code_of(source)

    quoted = set(re.findall(PATH_LITERAL, code))
    # A dot is an ordinary character in a path segment, because `Common.Web` is
    # a directory. An earlier form allowed one only at the start of a segment,
    # which quietly matched nothing in the one read this gate most depends on —
    # found by the test below rather than by reading it, which is the whole
    # argument for having that test. `..` is subtracted as the price: the
    # docstring's relative links to the blueprint are not reads.
    quoted = {r for r in quoted if ".." not in r.split("/")}
    # A PATH LITERAL IS ONE WHOSE FIRST SEGMENT EXISTS AT THE REPOSITORY ROOT.
    # Without that, `application/json` and `application/x-www-form-urlencoded`
    # -- the two media types `read_admin.py` sends -- are read as reads, and
    # the check demands a SOURCE_INPUTS entry for a MIME type. The rule is also
    # the precise one: a path nothing could open is not a read, and a new read
    # under a top-level directory that does not exist is a read of nothing.
    quoted = {r for r in quoted if (ROOT / r.split("/")[0]).exists()}

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
            "anywhere in deploy/keycloak, so it is the scan that is broken "
            "rather than the list that is complete"
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

    # BEFORE ANYTHING ELSE HOLDS IT. Every caller of this function reaches a
    # `print` eventually, and the one thing that must never reach one is a
    # credential — so the redaction is here rather than at each of them.
    return redact(document)


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

    authority = commands.add_parser(
        "authority", help="the realm to read, derived from the release's own values")
    authority.add_argument("--values", required=True, type=Path,
                           help="`helm get values <release> -o json` for the release being rolled")
    authority.add_argument("--trusted-origin", required=True,
                           help="the identity provider this deployment is willing to authenticate "
                                "to; the release's authority must name it")

    args = parser.parse_args(argv[1:])

    if args.command == "authority":
        try:
            document = json.loads(args.values.read_text(encoding="utf-8"))
        except OSError as error:
            raise SystemExit(f"realm-gate: {args.values} is not readable: {error}") from error
        except json.JSONDecodeError as error:
            raise SystemExit(f"realm-gate: {args.values} is not JSON: {error}") from error
        if not isinstance(document, dict):
            raise SystemExit(f"realm-gate: {args.values} is not a values document")

        root, realm = split_authority(authority_of(document))

        # THE ORIGIN IS PINNED AND THE REALM IS DERIVED, and mixing those up
        # either way is a hole. Deriving the realm is what stops the gate
        # checking a realm nobody is deploying to; deriving the *origin* would
        # hand the release's values control of where this job sends a client
        # secret — an authority of `https://attacker.example/realms/x` and the
        # credential is posted to that host's token endpoint. So the origin
        # comes from the deploy environment, the realm comes from the chart,
        # and the two are required to agree: a release pointed at an identity
        # provider this deployment does not trust stops the rollout rather than
        # authenticating to it.
        # BOTH SIDES ARE TRIMMED THE SAME WAY. Only the trusted value was,
        # so an authority of `https://host//realms/x` produced a root ending
        # in a slash and matched nothing an operator would ever type — a
        # rollout refused with a message that did not say why.
        trusted = args.trusted_origin.strip().rstrip("/")
        if root.rstrip("/") != trusted:
            raise SystemExit(
                f"realm-gate: the release is running with an authority on "
                f"{root!r}, and this deployment trusts {trusted!r}. Refusing "
                "to authenticate to an identity provider the deploy "
                "environment does not name.")

        # THE CHECK IS REPEATED ON THE OUTPUT, and the repetition is the
        # point. `split_authority` refuses whitespace in its input, so this
        # cannot fire today — but what must never contain a newline is what is
        # WRITTEN, and a future refusal relaxed on the input side would move
        # that guarantee somewhere this line cannot see. A subject test asserts
        # this line refuses, by handing it a value the parser would have
        # rejected.
        for value in (root, realm):
            if any(character.isspace() for character in value):
                raise SystemExit(
                    f"realm-gate: refusing to write {value!r} into the "
                    "environment: whitespace in a NAME=value line starts a "
                    "second assignment.")

        # Two `NAME=value` lines, for `>> $GITHUB_ENV`. Nothing else goes to
        # stdout on this path, because anything else would be read as one.
        # The base URL is written even though it equals the trusted origin the
        # caller already holds: `read_admin.py` requires all four names in the
        # environment, and writing the one this file verified is what makes the
        # verification load-bearing rather than advisory.
        print(f"{BASE_URL_VARIABLE}={root}")
        print(f"{REALM_VARIABLE}={realm}")
        return 0

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
