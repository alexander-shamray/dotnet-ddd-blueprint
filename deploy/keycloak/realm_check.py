#!/usr/bin/env python3
"""Every realm this platform is pointed at holds §11's token obligations.

[§11.3](../../docs/backend-architecture/11-identity-authorization.md) states a
300-second access-token lifetime and
[ADR-033](../../docs/backend-architecture/adr/ADR-033-revocation-is-bounded-by-the-token-lifetime-and-no-denylist-exists.md) composes the
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
from urllib.parse import urlsplit

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

# The deploy workflow is an input to this tree even though nothing here opens
# it at check time, and leaving it out was the sharper half of a review
# finding. `deploy.yml` is the ONLY caller that closes #157 — it derives the
# authority, fetches the realm and judges it, in that order — and the rollout
# job runs on `workflow_dispatch` alone, so a change that deleted or reordered
# those three steps would have gone through CI with this workflow skipped and
# `deploy.yml`'s own `check` job running only the canary's arithmetic.
#
# A gate that cannot see its own invocation is a gate that stops being invoked
# quietly, which is this repository's most-repeated failure with the call site
# rather than the subject as its object.
DEPLOY_WORKFLOW = ".github/workflows/deploy.yml"

# The canary plan is an input since ADR-043, and the workflow is the reader.
# `realm.yml`'s scheduled job loops over `canary.py workloads` to find every
# release whose realm it judges, so a change to the plan or to that subcommand
# changes the subject of this gate — and a subtree this workflow's triggers do
# not name is a change that reaches `main` with this gate skipped, which is
# the failure the whole SOURCE_INPUTS mechanism exists to refuse. Declared
# here and read by the suite, which asserts the subcommand the loop depends
# on still exists, on the deploy-workflow entry's own reasoning: a gate that
# cannot see its own invocation is a gate that stops being invoked quietly.
CANARY_PLAN = "deploy/canary"

SOURCE_INPUTS = [LIFETIME_SOURCE, COMPOSE_REALM, CHART_VALUES, DEPLOY_WORKFLOW, CANARY_PLAN]

WORKFLOW_PATH = ".github/workflows/realm.yml"

# The repository variable that opts the scheduled job in (ADR-043). Declared
# here because the workflow's `if:` and the README's provisioning step both
# spell it, and a name spelled in two places agrees until one is edited — the
# suite reads this constant out of the workflow rather than trusting either.
# A REPOSITORY variable rather than an Environment one, because a job-level
# `if` is evaluated before the job enters its Environment.
SCHEDULE_OPT_IN = "REALM_CHECK_SCHEDULED"

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

# `mobile-app` is the native client this route was opened for. No chapter
# fixes the name yet — the client is new rather than restated — so this
# constant is the first spelling of it and not a second one agreeing with a
# first. The same derivation trap applies as it does to BROWSER_CLIENT:
# inferring "the mobile client" from its flags would let the flags define the
# subject the flags are meant to be judged against.
MOBILE_CLIENT = "mobile-app"

# The two entries Keycloak accepts in `webOrigins` that are not origins, named
# because the check below has to tell them apart rather than refuse both alike.
# `*` answers every page on the internet; `+` means the origins this client's
# own redirect URIs imply, which is a real setting on a client redirected to an
# http(s) URL and an empty one on a client redirected to a custom scheme.
ORIGIN_WILDCARD = "*"
ORIGIN_FROM_REDIRECTS = "+"

# The schemes a redirect URI has to be on for `+` to derive anything from it. A
# browser sends the origin of the page that made the request, and no page is
# served from `blueprint://`.
WEB_SCHEME_NAMES = ("http", "https")

# A redirect URI's scheme, when it has one at all. The alternative reading of
# a URI with none is what this pattern exists to keep visible: Keycloak
# resolves a RELATIVE redirect against the client's `rootUrl` before taking an
# origin from it, so `/*` on a client with a `rootUrl` is a perfectly ordinary
# configuration whose origin this gate cannot see. Matching the scheme rather
# than the prefix is what lets `check_web_origins` tell "provably derives
# nothing" from "cannot tell", and refuse only the first.
REDIRECT_SCHEME = re.compile(r"^([A-Za-z][A-Za-z0-9+.\-]*):")

# Dropped from the canonical origin when the scheme implies them, because a
# browser drops them: `https://id.example.com:443` is never what arrives in an
# `Origin` header, so a realm entry spelling it matches nothing. Keycloak
# compares that header as text and has no opinion about a custom scheme's
# port, so only the two web schemes have a default to drop.
DEFAULT_PORTS = {"http": 80, "https": 443}

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

# EXACTLY WHAT THE CHECKS READ, AND THE GATE JUDGES NOTHING ELSE. Redacting the
# credential keys was not enough and CodeQL was right to keep saying so: the
# object still carried every other field of a realm, so any message formatting
# any part of it was a document with a `secret` key flowing into a `print`, and
# the property depended on a deny-list staying complete.
#
# A projection inverts that. `judged` builds a new document out of the named
# fields below, so what the checks hold has no credential in it to leak — not a
# redacted one, none — and a Keycloak version that adds a new secret-bearing
# field changes nothing here. The three tuples below are also the honest
# statement of what this gate reads.
#
# `revokeRefreshToken` and `refreshTokenMaxReuse` joined the realm fields with
# the mobile client rather than before it: a realm that issues no refresh
# token has nothing for either setting to bound, so the two were not this
# gate's business until a client existed whose refresh token they rotate.
#
# `redirectUris`, `publicClient`, `defaultClientScopes` and
# `pkce.code.challenge.method` joined on the same terms and for the same
# client: this is an extension point, not a closed set — the six keys that
# were here before `mobile-app` existed were never a ceiling, only what the
# checks read up to that point. On a secretless public client whose redirect
# target is a custom URI scheme any app on the device can register, these
# four are not incidental configuration: a widened `redirectUris` is direct
# token exfiltration, a `pkce.code.challenge.method` weakened to `plain` is
# the authorization-code interception attack PKCE exists to prevent, a
# `publicClient` flipped to `false` is a confidential-client posture the app
# cannot actually keep (it ships with no secret it can hide), and a dropped
# `commerce-api` scope is a token with no audience or permission claim.
#
# `webOrigins` joined last and on the same terms, and it is the one field here
# whose ABSENCE is the defect rather than a widening. Every other key above
# describes something a wrong value would let an attacker do; this one decides
# whether the client's own token exchange completes at all, and Keycloak's
# default is to grant nothing. A client that authenticates from a page — a
# browser SPA, or a packaged app's WebView — and declares no origin gets a
# token the browser then discards unread.
REALM_FIELDS = ("accessTokenLifespan", "revokeRefreshToken", "refreshTokenMaxReuse")
CLIENT_FIELDS = ("clientId", "standardFlowEnabled", "implicitFlowEnabled",
                 "directAccessGrantsEnabled", "publicClient", "redirectUris",
                 "defaultClientScopes", "webOrigins")
CLIENT_ATTRIBUTES = ("use.refresh.tokens", "access.token.lifespan",
                     "pkce.code.challenge.method")

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

# What Helm's `strvals` parser reads as structure rather than as text. The
# deploy step passes the derived authority as `--set-string
# identity.authority=<value>`, and `strvals` splits that on a COMMA whatever
# the shell quoting is — so an `identity.authority` of
# `https://host/realms/x,image.registry=attacker.example` is two assignments,
# one of which nobody checked. The backslash escapes, and the brackets and
# braces open list and map syntax.
#
# Refusing them in the authority is what makes the deploy step's `--set-string`
# safe, and the step says so rather than assuming it. It is the tag preflight's
# lesson one value over: `canary.py validate-tag` exists because exactly this
# parser turned one assignment into two.
STRVALS_METACHARACTERS = ",\\{}[]="

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


def judged(document: dict) -> dict:
    """A realm reduced to the fields this gate reads, and nothing else.

    Absence is preserved rather than defaulted, because half the checks turn on
    it — an absent `use.refresh.tokens` is ADR-034 violated, and an absent
    `accessTokenLifespan` is not 300. A key is copied only when the source has
    it, so "missing here" continues to mean "missing there".

    A client that is not an object survives as itself, because refusing one is
    `check_flags_are_booleans`'s job and a projection that dropped it would
    hide the malformed realm rather than judge it.
    """
    realm = {key: document[key] for key in REALM_FIELDS if key in document}

    clients: list = []
    for client in document.get("clients", []) if isinstance(document.get("clients"), list) else []:
        if not isinstance(client, dict):
            clients.append(client)
            continue
        narrowed = {key: client[key] for key in CLIENT_FIELDS if key in client}
        attributes = client.get("attributes")
        if isinstance(attributes, dict):
            narrowed["attributes"] = {
                key: attributes[key] for key in CLIENT_ATTRIBUTES if key in attributes}
        elif "attributes" in client:
            narrowed["attributes"] = attributes
        clients.append(narrowed)

    if "clients" in document:
        realm["clients"] = clients
    return realm


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

    found = sorted(set(authority) & set(STRVALS_METACHARACTERS))
    if found:
        raise SystemExit(
            f"realm-gate: identity.authority is {authority!r}, which contains "
            f"{''.join(found)!r}. Helm's strvals parser reads those as "
            "structure, and this value is passed to `--set-string` — a comma "
            "would make one assignment into two, and the second would be an "
            "override nobody checked.")

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

    mobile = [c for c in clients if isinstance(c, dict) and c.get("clientId") == MOBILE_CLIENT]
    if len(mobile) != 1:
        problems.append(
            f"the realm declares the native client {MOBILE_CLIENT!r} "
            f"{len(mobile)} time(s), expected exactly one. Its refresh-token "
            "obligation is a property of that client and cannot be checked "
            "without it")

    problems += check_flags_are_booleans(clients)
    problems += check_lifetime(realm, clients, lifetime)
    problems += check_implicit_flow(clients)
    if named:
        problems += check_browser_client(named[0], kind)
        problems += check_web_origins(named[0], BROWSER_CLIENT)
    if mobile:
        problems += check_mobile_client(mobile[0])
        problems += check_web_origins(mobile[0], MOBILE_CLIENT)
        problems += check_refresh_token_rotation(realm)
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
                    f"client {client.get('clientId')!r} sets {flag} to a "
                    f"{type(client[flag]).__name__} rather than a boolean. "
                    "Every check here compares against true or false, so a "
                    "value of any other type would be neither and would pass "
                    "unjudged")
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
            # THE VALUE IS NAMED, NOT ECHOED, and the key is why: an attribute
            # spelled `access.token.lifespan` is a token-shaped name, so a
            # message quoting whatever a realm put there is a credential-shaped
            # read reaching a log. The setting and the expectation are the
            # diagnostic; the string a misconfigured realm chose is not.
            problems.append(
                f"client {client.get('clientId')!r} sets "
                "access.token.lifespan to something that is not a number of "
                "seconds. This gate cannot say what lifetime that client "
                "issues, which is not the same as saying it is the realm's")
        # An override equal to the realm value is not a finding. It is
        # redundant rather than wrong, and failing it would make this gate
        # refuse a realm that holds the obligation it exists to enforce.
        elif int(seconds) != lifetime:
            # The PARSED number, not the source text. A count of seconds is the
            # diagnostic and cannot carry anything else.
            problems.append(
                f"client {client.get('clientId')!r} sets "
                f"access.token.lifespan to {int(seconds)} seconds, overriding "
                f"the realm's {lifetime}. A client-level lifespan is the "
                "misconfiguration this gate was filed for")
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
            f"client {BROWSER_CLIENT!r} sets use.refresh.tokens to something "
            "other than \"false\". ADR-034 gives the browser an access token "
            "and no refresh token")

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


def check_mobile_client(client: dict) -> list[str]:
    """The native client's whole shape, not only the refresh-token half of it.

    `web-app` runs in a browser, where a refresh token is reachable by any
    script on the origin — ADR-034's reason to withhold one. `mobile-app` runs
    as a platform-installed app with no equivalent script-injection surface,
    so the realm issues it a refresh token rather than the browser's none, and
    what has to hold instead is rotation: `check_refresh_token_rotation` below
    is what makes a copied refresh token cost something once one exists to
    copy.

    Unlike `check_browser_client`, the password-grant check here does not take
    the realm kind: the client JSON this PR adds sets
    `directAccessGrantsEnabled` to `false` unconditionally, because nothing
    documents a password-grant login for the native client in either realm —
    §14.1's curl recipe is `web-app`'s alone.

    The four checks below this function's first draft omitted are not a
    second tier of the obligation — on a secretless public client a wrong
    answer to any one of them is a live exploit, not a configuration
    preference, in ascending severity:

    - `pkce.code.challenge.method` weakened from `S256` to `plain` (or
      dropped) reopens the authorization-code interception attack PKCE
      exists to close, on the one client in this realm with no secret to
      fall back on.
    - `redirectUris` widened — a second entry, a wildcard, an `http://`
      scheme alongside the custom one — is a code (and, given the refresh
      token this client holds, ultimately a token) delivered to whatever
      app or page the widened pattern also matches.
    - `defaultClientScopes` missing `commerce-api` mints a token with no
      audience and no permission claim, silently, for the one caller this
      realm expects to carry both.
    - `publicClient` flipped to `false` claims a confidential-client posture
      this app cannot keep: it ships with no secret it could hide, so
      Keycloak's client-secret authentication would be satisfied by a value
      baked into every install of it.
    """
    problems: list[str] = []
    attributes = client.get("attributes")
    attributes = attributes if isinstance(attributes, dict) else {}

    refresh = attributes.get("use.refresh.tokens")
    if refresh is None:
        problems.append(
            f"client {MOBILE_CLIENT!r} declares no use.refresh.tokens "
            "attribute. Keycloak's default already issues one on the standard "
            "flow, so the realm happens to comply today — but an unstated "
            "attribute is the same silence ADR-034 refuses for the browser, "
            "read the other way round, and a future Keycloak default is not "
            "this gate's to trust")
    elif str(refresh).lower() != "true":
        problems.append(
            f"client {MOBILE_CLIENT!r} sets use.refresh.tokens to something "
            "other than \"true\". The native client has no way to obtain a "
            "fresh access token without one once the short-lived one expires")

    # The positive half, on check_browser_client's own reasoning: without the
    # standard flow there is no authorization-code exchange and therefore no
    # refresh token either, whatever the attribute above says.
    if client.get("standardFlowEnabled") is not True:
        problems.append(
            f"client {MOBILE_CLIENT!r} does not enable the standard flow. "
            "The refresh-token obligation above then holds because the "
            "client mints no token at all, which is not the guarantee this "
            "route was added for")

    grants = client.get("directAccessGrantsEnabled")
    if grants is not False:
        problems.append(
            f"client {MOBILE_CLIENT!r} has directAccessGrantsEnabled="
            f"{grants!r}. The native client authenticates through the "
            "authorization-code flow with PKCE; nothing documents a password "
            "grant for it, in either realm kind")

    method = attributes.get("pkce.code.challenge.method")
    if method != "S256":
        problems.append(
            f"client {MOBILE_CLIENT!r} sets pkce.code.challenge.method to "
            f"{method!r}, not \"S256\". This client holds no secret, so PKCE "
            "is the only thing standing between an intercepted authorization "
            "code and the attacker who intercepted it — 'plain' or absent "
            "both leave that door open")

    if client.get("publicClient") is not True:
        problems.append(
            f"client {MOBILE_CLIENT!r} has publicClient="
            f"{client.get('publicClient')!r}. It ships with no secret it "
            "could keep confidential — every install of the app carries the "
            "same one — so a client-secret posture is a credential baked "
            "into the binary rather than a real one")

    redirects = client.get("redirectUris")
    if redirects != ["blueprint://auth/callback"]:
        problems.append(
            f"client {MOBILE_CLIENT!r} has redirectUris={redirects!r}, not "
            "exactly [\"blueprint://auth/callback\"]. Widening this — a second "
            "entry, a wildcard, an http(s) scheme beside the custom one — is "
            "a code, and through it a refresh token, delivered wherever the "
            "wider pattern also matches")

    scopes = client.get("defaultClientScopes")
    if not isinstance(scopes, list) or "commerce-api" not in scopes:
        problems.append(
            f"client {MOBILE_CLIENT!r} does not hold commerce-api as a "
            "default client scope. A client-issued token with this scope "
            "missing carries no audience and no permission claim, and every "
            "request it makes is refused with nothing in this file's own "
            "checks to say why")
    return problems


def canonical_origin(text: object) -> str | None:
    """The origin a browser would send for this text, or `None` if it is not one.

    `Gateway.Api/Program.cs` arrived here from six review rounds of enumerating
    the ways a string can fail to be an origin — blank, `*`, unparseable, a
    trailing slash, a path, a default port — and replaced all six with one
    equality against the canonical form, because the ways a string can BE an
    origin are finite and the ways it can fail are not. This is that equality
    in Python, for the same reason: Keycloak compares the `Origin` header as
    text, so an entry that is not character-for-character what a browser sends
    matches nothing and grants nothing.

    **The authority is rebuilt rather than compared as it arrived**, and the
    first draft of this function compared it as it arrived. `urlsplit` does not
    look at the port until `.port` is read, so against a raw `netloc` all four
    of `:not-a-port`, `:70000`, a bare trailing colon and a zero-padded `:0443`
    compared equal to themselves and passed — four spellings no browser can
    send, through a check whose entire purpose is that only what a browser
    sends gets through. Reading `.port` is what refuses the first two;
    rebuilding the authority from `hostname` and `port` is what refuses the
    other two, because surviving a round trip through the parser is the
    property "already canonical" actually means.

    A custom scheme passes, which is the one place this parts company with the
    gateway's version, and the difference is in what consumes the value rather
    than in taste. `WithOrigins` feeds ASP.NET Core's own matcher and the
    gateway refuses anything but http(s) at startup; a realm entry is a string
    Keycloak compares, and a packaged WebView really does present one.
    Refusing it here would refuse the origin this whole obligation exists for —
    and it is also why the default port is looked up by scheme rather than
    assumed, since a custom scheme implies none.
    """
    if not isinstance(text, str):
        return None
    try:
        parts = urlsplit(text)
        port = parts.port
    except ValueError:
        return None
    if not parts.scheme or parts.username is not None:
        return None

    host = parts.hostname
    if not host:
        return None

    scheme = parts.scheme.lower()
    # An IPv6 literal is bracketed in an origin and unbracketed by `hostname`.
    authority = f"[{host}]" if ":" in host else host
    if port is not None and port != DEFAULT_PORTS.get(scheme):
        authority = f"{authority}:{port}"

    canonical = f"{scheme}://{authority}"
    return canonical if canonical == text else None


def redirects_cannot_imply_an_origin(client: dict) -> bool:
    """True only where `+` PROVABLY derives nothing from this client.

    The question is deliberately asked in the direction that fails silent
    rather than the direction that fails loud, and the first draft asked the
    other one. It tested whether any redirect URI *started with* `http://` or
    `https://` and reported `+` as empty whenever none did — which is wrong on
    the ordinary Keycloak configuration this gate will meet in a deployed
    realm, because a relative redirect like `/*` is resolved against the
    client's `rootUrl` before an origin is taken from it. That client is
    correctly configured and would have been failed by this gate.

    So a URI with no scheme is not evidence of anything and returns False:
    this gate does not hold `rootUrl` and will not guess at what it resolves
    to. What remains provable is the case this obligation was filed about — a
    client every one of whose redirect URIs is absolute and on a scheme no
    page is served from, where `+` resolves to nothing at all while reading in
    a console as though the question had been answered.

    The cost is one case knowingly let through: a redirect of `https://` with
    no host after it satisfies the web-scheme test and yields no usable origin
    either. Mirroring Keycloak's resolution properly would catch it, and was
    refused — it means projecting `rootUrl`, reimplementing a resolution this
    repository does not own, and being wrong about it in a gate whose entire
    argument is that it asserts only what it can see.
    """
    redirects = client.get("redirectUris")
    if not isinstance(redirects, list) or not redirects:
        return False
    for uri in redirects:
        if not isinstance(uri, str):
            return False
        scheme = REDIRECT_SCHEME.match(uri)
        if scheme is None or scheme.group(1).lower() in WEB_SCHEME_NAMES:
            return False
    return True


def check_web_origins(client: dict, client_id: str) -> list[str]:
    """The origin the client's own page sends, which is not its redirect URI.

    Two fields, two hops, and conflating them is how this obligation went
    missing. `redirectUris` is where Keycloak sends the authorization code — a
    system-browser redirect back into the app, and `blueprint://auth/callback`
    is right for it. `webOrigins` decides whose script Keycloak will let READ a
    token response, and the exchange is a second request the page makes itself:
    from the WebView's own loopback origin in a packaged app — which differs by
    platform and is the realm export's to state — and from the site's own
    origin in a browser. Getting the first right says nothing about the second.

    The failure it catches produces no error anywhere, which is why it wants a
    gate rather than a test. `application/x-www-form-urlencoded` is
    CORS-safelisted and a public client sends no `Authorization` header, so
    there is no preflight to fail: the request goes out, Keycloak mints a
    token, and the browser discards the response because no
    `Access-Control-Allow-Origin` came back with it. The authorization code is
    spent, `fetch` rejects with a `TypeError`, and the server, the suite and CI
    all stay green. It was read out of the realm rather than observed on a
    device —
    https://github.com/alexander-shamray/blueprint-frontend/issues/6.

    THE OBLIGATION IS ASSERTED AND THE VALUES ARE NOT, which is a decision
    rather than a shortcut. What a packaged app's browser origin actually is comes
    out of `capacitor.config.ts` in the other repository — `androidScheme` and
    `iosScheme`, both of which that repository is reconsidering as this is
    written — so a literal pair here would pin this gate to another repo's
    current default and fail a realm that had been corrected rather than one
    that had drifted. What this gate can see without owning that file is the
    shape, and the shape is where all three silent failures live: nothing
    granted, everything granted, and an entry no browser will ever send.

    It judges both named clients rather than the native one alone. The
    obligation belongs to the token exchange and not to a platform: `web-app`
    runs the same exchange from a browser, satisfies this today, and would
    break the same silent way if someone cleared the field in a console.
    Keycloak's own built-in clients are not judged, here or anywhere in this
    file — `account` and `account-console` ship with no origins and need none.
    """
    problems: list[str] = []
    origins = client.get("webOrigins")

    if origins is None:
        return [
            f"client {client_id!r} declares no webOrigins. Keycloak answers a "
            "token request with no Access-Control-Allow-Origin unless the "
            "client grants one, and a browser discards a response it may not "
            "read — so the exchange succeeds, the authorization code is spent, "
            "and the sign-in fails with nothing on either side saying why"
        ]
    if not isinstance(origins, list):
        return [
            f"client {client_id!r} has a webOrigins that is not an array. "
            "Keycloak serialises this field as one in an export and in an "
            "admin-API answer alike, so anything else is a hand-edited realm "
            "rather than a configured one"
        ]
    if not origins:
        return [
            f"client {client_id!r} declares an empty webOrigins, which grants "
            "CORS to nothing at all. That is the absent field above written "
            "out, and it is worse for reading in a console as though the "
            "question had been answered"
        ]

    if ORIGIN_WILDCARD in origins:
        problems.append(
            f"client {client_id!r} declares {ORIGIN_WILDCARD!r} as a web "
            "origin, which answers every page on the internet with "
            "Access-Control-Allow-Origin: *. The token endpoint still demands "
            "an authorization code and its PKCE verifier, so this is a "
            "widening rather than an exploit — but it is one no client here "
            "needs, and naming the origins costs a line")

    if ORIGIN_FROM_REDIRECTS in origins and redirects_cannot_imply_an_origin(client):
        problems.append(
            f"client {client_id!r} declares {ORIGIN_FROM_REDIRECTS!r} as a "
            "browser origin, which means the origins its own redirectUris "
            "imply — and every one of them is absolute and on a scheme no page "
            "is served from, so they imply none. At runtime that is an empty "
            "webOrigins; in a console it reads as a setting")

    malformed = [index for index, origin in enumerate(origins)
                 if origin not in (ORIGIN_WILDCARD, ORIGIN_FROM_REDIRECTS)
                 and canonical_origin(origin) is None]
    if malformed:
        problems.append(
            f"client {client_id!r} has a webOrigins entry that is not a "
            f"browser origin at index {', '.join(str(i) for i in malformed)}. "
            "One is a scheme, a host, and a port only when it is not the "
            "scheme's default — exactly as a browser serialises it — because "
            "Keycloak compares the Origin header as text and anything else "
            "matches nothing. The value is deliberately not echoed: userinfo "
            "survives the authority form, and a guard that rejects a "
            "credential must not be the thing that publishes it")
    return problems


def check_refresh_token_rotation(realm: dict) -> list[str]:
    """Realm-wide rotation, which is what makes a stolen refresh token cost something.

    `revokeRefreshToken` and `refreshTokenMaxReuse` are realm settings with no
    per-client override in Keycloak, so they are checked once here rather than
    folded into `check_mobile_client` — a realm that got rotation right for
    one client and wrong for another is not a shape Keycloak can produce.

    Every client in this realm holds a refresh token to rotate except one, and
    the one is not `web-app` — ADR-034 already leaves the browser none, which
    makes it an easy exception. It is `web-bff`: a confidential, server-side
    client with a secret worth protecting, which is exactly where
    `refreshTokenMaxReuse: 0` would bite hardest on a pair of concurrent
    requests racing to refresh the same token. `web-bff` is out of range on a
    narrower fact than "it is not the browser" — `standardFlowEnabled: false`
    (`RealmClientTests.No_flow_can_obtain_a_token_as_a_person_through_it`)
    means it runs no authorization-code flow and so is issued no refresh
    token of that kind to rotate in the first place; its own tokens come from
    the client-credentials grant, which mints none. Every other standard-flow
    client in the shipped realm is one of Keycloak's own built-in consoles
    (`account`, `security-admin-console`, and the rest), which are built to
    tolerate rotation — so realm-wide is not a second exposure for anything
    that is actually exposed by it.

    Called only once a mobile client has been found (`check_realm`'s `if
    mobile:` guard), on the same reasoning `check_browser_client` is guarded
    by `if named:` — a rotation setting is only an obligation once a client
    exists for it to bind.
    """
    problems: list[str] = []

    revoke = realm.get("revokeRefreshToken")
    if revoke is not True:
        problems.append(
            f"revokeRefreshToken is {revoke!r}, and {MOBILE_CLIENT!r} is "
            "issued a refresh token. Without rotation, a refresh token copied "
            "once keeps minting access tokens for as long as the session "
            "lasts, whatever ADR-033's access-token bound says about the "
            "token it mints")

    reuse = realm.get("refreshTokenMaxReuse")
    if reuse != 0:
        problems.append(
            f"refreshTokenMaxReuse is {reuse!r}, not 0. A nonzero value lets "
            "a refresh token already rotated out of use be replayed that "
            "many more times, which is exactly the reuse window rotation "
            "exists to close")
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

    # PROJECTED BEFORE ANYTHING ELSE HOLDS IT. Every caller of this function
    # reaches a `print` eventually, and the one thing that must never reach one
    # is a credential — so the narrowing is here rather than at each of them,
    # and it is a narrowing rather than a redaction because a deny-list is only
    # as good as its next entry.
    return judged(redact(document))


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
        # BOTH SIDES ARE TRIMMED THE SAME WAY. Only one of them was, so an
        # authority of `https://host//realms/x` produced a root ending in a
        # slash and matched nothing an operator would ever type — a rollout
        # refused with a message that did not say why.
        #
        # `expected` and not `trusted`, for the reason the suite's own helper
        # gives: `py/clear-text-logging-sensitive-data` classifies by the name
        # holding a value and reads `trusted` as a secret. This one holds an
        # origin, and the local says so.
        expected = args.trusted_origin.strip().rstrip("/")
        if root.rstrip("/") != expected:
            raise SystemExit(
                f"realm-gate: the release is running with an authority on "
                f"{root!r}, and this deployment names {expected!r}. Refusing "
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
