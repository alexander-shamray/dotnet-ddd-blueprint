# The realm gate

**What §11's token obligations are, which realms are held to them, and what
this tree deliberately does not check.**

[ADR-042](../../docs/backend-architecture/appendix-a-adrs.md#adr-042--the-deployed-realm-is-checked-at-deploy-time)
is the decision; [§15.1](../../docs/backend-architecture/15-cicd-deployment.md)
lists this as the fifth `deploy/**` subtree CI exercises rather than deploys.
This file is that tree's operational reference, on
[`deploy/observability/README.md`](../observability/README.md)'s terms.

## The two subjects

| | |
|---|---|
| `realm.yml` | `deploy/compose/keycloak/realm-export.json` — [§14.1](../../docs/backend-architecture/14-local-development.md)'s Compose realm, on every change to it, to `AuthenticationExtensions.cs` or to this tree |
| `deploy.yml` | the realm a deployment is about to be rolled onto — **derived from the chart**, fetched by `read_admin.py`, and judged before the rollout changes anything |

**One predicate judges both**, because a Keycloak realm export and the admin
API's `RealmRepresentation` are the same document — the export is that
representation serialised, and the client list a full export carries under
`clients` is what `GET /admin/realms/{realm}/clients` answers.

```bash
py -3.12 -m unittest discover -s deploy/keycloak
py -3.12 deploy/keycloak/realm_check.py inputs
py -3.12 deploy/keycloak/realm_check.py check --kind local
```

## What it asserts

- `accessTokenLifespan` equals the `AccessTokenLifetime` `Common.Web` declares.
  **Read out of that declaration**, never restated —
  [ADR-040](../../docs/backend-architecture/appendix-a-adrs.md#adr-040--no-host-accepts-a-token-with-more-life-left-than-the-revocation-bound)
  made it the one place the number lives.
- No client carries an `access.token.lifespan` attribute disagreeing with it.
  Keycloak resolves the client attribute over the realm value, so a realm at
  300 with one client at 18000 issues five-hour tokens to that client.
- No client enables the implicit flow, which is what makes
  `accessTokenLifespanForImplicitFlow` unreachable and its silence honest.
- `web-app` carries `use.refresh.tokens` `"false"`
  ([ADR-034](../../docs/backend-architecture/appendix-a-adrs.md#adr-034--the-browser-holds-an-access-token-and-no-refresh-token)),
  and carries it — an absent attribute is the violation, because Keycloak's
  default is to issue refresh tokens on the standard flow.
- `web-app` enables the standard flow, without which the line above holds
  because the client mints nothing at all.
- `directAccessGrantsEnabled` is **off in a deployed realm and on in the local
  one**. That is the one obligation that inverts, which is why `--kind` is
  required and has no default: §11.2 documents the password grant as a local
  affordance a deployed realm turns off, and §14.1's documented login *is* that
  grant.

## What it does not check

- **`ClockSkew`.** It is the other half of ADR-033's 330 and it is not a realm
  setting at all — it is `Common.Web`'s, pinned by `JwtAuthenticationTests`.
  An operator told to configure a realm `ClockSkew` would go looking for
  something that does not exist.
- **Everything else in the realm — and it does not merely decline to check
  it, it does not hold it.** What the gate judges is a projection of six named
  fields, so the audience mapper, the permission vocabulary, the client scopes,
  the two development logins and every client secret are not in the object at
  all. Those belong to `tests/Common.Web.Tests/RealmImportTests.cs`, which is
  not superseded. The projection is also why no message here can leak a
  credential: there is none to leak.
- **A realm with more clients than the ceiling.** The client list is read in
  one request asking for far more than any realm this platform will have, and a
  response *at* that ceiling stops the run rather than being truncated.
- **Whether the answer was filtered — directly.** Keycloak applies `max` to the
  client-model stream and then drops the representations the caller may not
  see, so a filtered list and a complete one look the same and no ceiling or
  page boundary can tell them apart. What is checked instead is the premise:
  `read_admin.py` reads the roles out of its own token and stops before asking
  unless the account holds `view-clients` or `realm-admin`. The
  token is decoded and **not verified**, which is safe because nothing is
  authorised on it — the server issued it and the server enforces the roles;
  it is read to find out whether this account could see the whole realm.
  **`view-realm` is not accepted** and reads as though it should be: Keycloak
  defines it as a non-composite role granting nothing else, so it carries no
  client visibility. The suite asserts that against §14.1's export rather than
  restating it here.
- **Whether the realm is reachable outside a rollout.** It is read when a
  deployment reads it, so a realm edited between rollouts is unobserved until
  the next one
  ([#176](https://github.com/alexander-shamray/dotnet-ddd-blueprint/issues/176)).
  What bounds that window is ADR-040's runtime guard, not this tree.
- **Which realm a *future* rollout will install.** The authority is read from
  the release as it stands, so a rollout that also changes `identity.authority`
  checks the realm it is leaving. Nothing here does that — `-f
  stable-values.yaml` carries the value forward — but a change that started
  moving authorities would owe this file a second look.
- **Anything about a cluster.** The deploy half has never run, because
  `deploy.yml` has never reached one and says so in its own header. What has
  been executed is the local half — which is exactly why the local half exists:
  a deploy-time check nothing has ever run is a check nobody has established is
  looking at anything.

## The credential

`read_admin.py` needs four values and stops naming every one that is missing,
and they arrive from two different places on purpose.

**Two are configured.** `KEYCLOAK_CHECK_CLIENT_ID` and
`KEYCLOAK_CHECK_CLIENT_SECRET` come from the `production` GitHub Environment;
[`docs/secrets.md`](../../docs/secrets.md) carries how the service account is
provisioned and rotated, and why it is the one credential here that reaches no
row of §15.4's table.

**Two are derived.** `KEYCLOAK_BASE_URL` (the **server root**, not the realm's
issuer URL) and `KEYCLOAK_REALM` are written into `$GITHUB_ENV` by
`realm_check.py authority`, out of the `identity.authority` the release being
rolled is running with. A realm named beside the chart rather than out of it is
a check that can pass on a realm nobody is deploying to.

**And one pins where the credential may be sent.** `KEYCLOAK_TRUSTED_ORIGIN` is
a third Environment variable, and it is what keeps the derivation above from
being worse than the hole it closed: `identity.authority` lives in the cluster,
so deriving the *origin* from it would let whoever can edit a release have this
job post its client secret to a host of their choosing. The origin is pinned
here, the realm is derived there, and `authority` refuses a release whose
authority names any other origin.

A base URL that is not `https` is refused: the bearer token that fetch obtains
can read every client secret in the realm. So is a redirect — `urllib` carries
an `Authorization` header onto a redirected request, and the `https` check says
nothing about where a 302 leads.
