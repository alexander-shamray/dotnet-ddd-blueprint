# ADR-044 — The native client holds a refresh token, and the realm rotates it

**Decision.** [§11.2](../11-identity-authorization.md)'s native client —
`mobile-app`, the Android/iOS client the Ionic/Capacitor reference build
authenticates as — is issued a refresh token where the browser client is not
([ADR-034](ADR-034-the-browser-holds-an-access-token-and-no-refresh-token.md)):
its realm attribute is `use.refresh.tokens: "true"` against `web-app`'s
`"false"`. In exchange the realm turns rotation on: `revokeRefreshToken`
moves from `false` to `true`, and `refreshTokenMaxReuse` stays `0`.
`deploy/keycloak/realm_check.py`'s `check_mobile_client` pins the first half
and `check_refresh_token_rotation` the second, both reachable only once a
client named `mobile-app` exists in the realm being judged.

**Why.** ADR-034's refusal is about a specific storage: `localStorage`,
`sessionStorage`, an in-memory variable — anything on `web-app`'s origin,
readable by any script that shares it (an XSS, a compromised transitive
dependency in the bundle) and, because a browser's profile is not itself
encrypted per origin, by anything with filesystem or backup access to the
machine it runs on. `mobile-app`'s refresh token is written to the Android
Keystore or the iOS Keychain instead, through
`@aparajita/capacitor-secure-storage` — a store neither web storage nor an
in-memory variable has an equivalent of, and one that protects against a
different set of attackers: another app on the same device, extraction from
a filesystem image or a device backup, and an attacker who has the device
but not the app's own unlocked process.

> **What that storage does not protect against is the app's own JavaScript,
> and the distinction has to be stated rather than assumed — the first draft
> of this decision got it wrong.** `mobile-app` authenticates a
> Capacitor/Ionic hybrid, and the secure-storage plugin above is a
> JavaScript-callable bridge: a compromised transitive dependency in the
> Angular bundle can call it and read the refresh token out of the Keychain
> exactly as the equivalent dependency could read `web-app`'s out of
> `localStorage`. The Keystore and the Keychain are not an immunity to the
> threat ADR-034 names; they narrow *which* attackers reach the token, from
> "any script on the origin, or anyone with the machine" to "this app's own
> compromised code, and nothing else that is not the device's owner with the
> app unlocked." That narrower set is still the whole of the argument: it is
> real, and it is why `mobile-app` may hold what `web-app` may not, without
> claiming the hybrid architecture is safe from the vulnerability class
> ADR-034 was written about.

So this is a **prohibition gaining an exception**
([`docs/change-locality.md`](../../change-locality.md)'s own name for a
Class C change) rather than a restatement of ADR-034: the rule that a browser
holds no refresh token is unchanged and un-superseded, and what is decided
here is that a client whose storage answers a materially different threat
model is not bound by a rule written for the storage it does not have.

**Rotation is the trade, and it is realm-wide because Keycloak gives it no
narrower scope.** `revokeRefreshToken` and `refreshTokenMaxReuse` are realm
settings with no per-client override, so turning either on changes behaviour
for every client that holds any refresh token, not `mobile-app` alone. A
refresh token becomes usable exactly once under `refreshTokenMaxReuse: 0`:
presenting one that has already been used does not mint another access
token, it revokes the session the token belonged to. A refresh token copied
off a device — a stale backup, another app reading this one's Keystore entry
through a platform bug — is a token the legitimate app has already used or
will use next; whichever of the two presents it second is refused, and the
session both were drawing from ends there. That is a real cost imposed on
every other client with a refresh token to lose, and turning it on for one
client's benefit is only sound because there is, in this realm, no other
client it actually costs anything: `web-bff` is a standard-flow candidate by
neither its protocol nor its purpose — `standardFlowEnabled: false`
(`RealmClientTests.No_flow_can_obtain_a_token_as_a_person_through_it` pins
it) means it runs no authorization-code flow and is issued no refresh token
of that kind to rotate in the first place, its own tokens coming from the
client-credentials grant, which mints none. Every other standard-flow client
in the shipped realm is one of Keycloak's own built-in consoles (`account`,
`security-admin-console`, and the rest), which are built to tolerate
rotation as a matter of course.

**Consequences.**

- **The realm's two settings are checked once a native client exists to bind
  them, not unconditionally.** `check_refresh_token_rotation` runs only when
  `check_realm` has found exactly one `mobile-app` client, on the same
  reasoning `check_browser_client` is guarded by the browser client's own
  presence — a rotation setting is an obligation only once a client exists
  for it to be an obligation *of*.
- **`mobile-app`'s own shape is checked beyond the refresh-token attribute.**
  `check_mobile_client` also pins `standardFlowEnabled: true`, no direct
  access grants in either realm kind (§14.1's password-grant local
  affordance is `web-app`'s alone — nothing documents one for the native
  client), `publicClient: true` (it ships with no secret it could keep
  confidential), `pkce.code.challenge.method: "S256"` (the one thing standing
  between an intercepted authorization code and the attacker who intercepted
  it, on a client with no secret to fall back on), the exact redirect URI
  `blueprint://auth/callback`, and `commerce-api` among its default client
  scopes. None of these were asserted anywhere in the repository before this
  record, and each absence is a silent regression a Keycloak console click
  could have caused.
- **`web-app` is unaffected by either rotation setting**, for the reason
  ADR-034 already gives: it holds no refresh token, so a rule about rotating
  one binds nothing that belongs to it.
- **This is the local realm's decision, and a deployed realm owes it the same
  way ADR-034's and ADR-042's obligations are owed** — this repository
  provisions no realm and owns only the Compose export;
  [ADR-042](ADR-042-the-deployed-realm-is-checked-at-deploy-time.md)'s
  deploy-time check and
  [ADR-043](ADR-043-the-deployed-realm-is-checked-between-rollouts.md)'s
  scheduled one read `realm_check.py`'s same predicate against whichever
  realm a deployment points at, and a realm missing `mobile-app`, its
  attributes, or the rotation pair now fails the check that ADR-042 and
  ADR-043 already wired to a rollout and a schedule, rather than shipping the
  omission with the suite green.
- **The client this record decides about did not originate as a decision
  made in this repository.** `mobile-app` was added to the realm by a
  backend pull request in service of the Angular/Ionic reference client
  under `blueprint-frontend`, a sibling repository, whose design document
  `docs/superpowers/specs/2026-09-10-blueprint-frontend-design.md`, §9
  ("Backend dependency: the `mobile-app` client"), specifies the client's
  exact attributes. This record is what makes accepting that specification a
  decision this repository took rather than a fact it merely absorbed —
  [§11.2](../11-identity-authorization.md) is the chapter section it amends,
  and cites it rather than restating either setting's literal value.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
