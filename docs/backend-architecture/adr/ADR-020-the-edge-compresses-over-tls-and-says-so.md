# ADR-020 — The edge compresses over TLS, and says so

**Decision.** `Gateway.Api` registers `AddResponseCompression` with
`EnableForHttps = true` and calls `UseResponseCompression` in its pipeline —
both halves are named because the registration on its own compresses nothing.
It takes the framework's default providers — Brotli and Gzip at
`CompressionLevel.Fastest` — and its default compressible type list, which does
**not** include `application/problem+json`. It replaces the framework's
`IResponseCompressionProvider` with one that refuses any response carrying
`Cache-Control: no-transform`, which RFC 9111 requires of an intermediary and
ASP.NET Core does not implement. No other host in the platform compresses
anything.
**Why.** The framework ships `EnableForHttps = false` because compressing a
response that mixes attacker-influenced input with a secret leaks the secret's
length, which is BREACH. **Not CRIME**, which attacked compression in the TLS
layer rather than of an HTTP response body, and naming both would conflate two
layers in the one paragraph deciding what this edge compresses.
**Here that flag is what makes compression
happen at all**, and the first version of this ADR argued the exact opposite.
It reasoned that TLS terminates at the load balancer or Ingress
([§10.1](../10-api-gateway.md)) and plain HTTP is forwarded inside the cluster, so
the gateway is served `http` and the flag never fires. Every clause of that is
true except the conclusion. §4.2's forwarded-headers block enables
`XForwardedProto`, `UseForwardedHeaders` rewrites `Request.Scheme` from the
ingress's header, and the compression middleware takes its decision at the
first **write** — below the whole pipeline — so the scheme it reads is the
rewritten one. Left at its default, a gateway behind an HTTPS ingress
compresses **nothing**, and no response says why.
`ForwardedSchemeCompressionTests` is the measurement; it goes red against the
property removed.

So the flag cannot be argued from the scheme in either direction: the
*response* reaches the browser over TLS whatever the inner hop was, and the
inner hop is not what the middleware reads anyway. It has to be argued from
content, and the content is what makes it safe: the bodies crossing this edge
are proxied API JSON, and the platform puts no secret in one. Tokens are
issued by Keycloak and reach the gateway on an `Authorization` header rather
than in a body ([§11.2](../11-identity-authorization.md)), no response sets a
session cookie, and no endpoint returns an anti-forgery token.
The one body that *does* reflect a client-supplied value back — §10.5's
problem+json, carrying the `X-Correlation-Id` the caller may have chosen
(§10.4) — is the one the default type list omits, so the input half and the
compression never meet.
**Consequences.** The gateway now spends CPU per response — which is precisely
the resource §15.3 deliberately leaves *unlimited*, because CPU is compressible
and a cap on it surfaces as unexplained p99 spikes long before the pod is short
of capacity. Memory is the one §15.3 bounds, and what to size it against is
**concurrent compressed responses** — each holds a compressor and its
buffers for the life of the response. Explicitly *not* §10.1's body ceiling:
that bounds a request, and nothing about it constrains how large a proxied
response is or how many are in flight. So an edge latency
regression is investigated as CPU spent here and never as a leak, and a
compression provider is the first thing to look at. The omission of
`application/problem+json` is a framework default this platform relies on and
does not state, so `CompressedResponseTests` pins it from the wire in both
directions — adding the type to
`CompressibleContentTypes` would be re-taking this decision, and the test is
what makes that visible. **The rule is inherited rather than re-decided by
every host behind the edge**: PR-19's BFF is the first that could hold a
session, and its responses pass through this middleware. A BFF response
carrying a secret says so with **`Cache-Control: no-transform`**, and
`Gateway.Api` honours it through a `ResponseCompressionProvider` of its own.

**That is a conformance fix, not a preference.** RFC 9111 §5.2.2.6 says the
directive "indicates that an intermediary (regardless of whether it implements
a cache) MUST NOT transform the content", and applying a content coding is such
a transformation (RFC 9110 §7.7). A YARP gateway is an intermediary. ASP.NET
Core's middleware does not implement the rule — measured, before the provider
existed: a body sent under the directive came back gzipped with the directive
intact — so the edge was violating it on every such response.

**The request form is honoured too, and it is a weaker thing.** §5.2.1.6 says
only that "the client is asking for intermediaries to avoid transforming the
content" — an ask, where the response form is an obligation. The provider
refuses either, because a caller who says so explicitly should be believed and
the check is one header read. The asymmetry is written down rather than
flattened into "the RFC requires it", which would be false of half of it.

**Reading the request header costs a `Vary` entry, and forgetting it would hand
the policy back to any cache in front.** The representation now depends on
`Cache-Control`, so the provider advertises it as a cache-selection dimension
on every decision — including the compressed ones, because a response
compressed *because* no directive arrived varies on the header exactly as a
refused one does. The price is cache efficiency, since callers send assorted
`Cache-Control` values; the alternative is a shared cache serving a stored
gzipped variant to the one caller who asked for none.

**A destination's `Vary: *` is left alone, and the framework's own entry is
not.** The wildcard covers every dimension, so adding a field name beside it
narrows nothing; the provider checks before appending, which is the idiom the
middleware already uses for `Accept-Encoding`. What the middleware does *not*
do is check for the wildcard — it appends `Accept-Encoding` regardless,
after the provider has answered and through no seam the provider can reach, so
a destination's `*` reaches the client as `*, Accept-Encoding`. Measured and
recorded rather than asserted as correct: it is the framework's behaviour, not
this platform's decision, and the test says which is which.

`Content-Encoding: identity` also stops the middleware, and is **not** the
contract offered here. It works only as a side effect of the
double-compression guard — a refusal reached by looking like an already-encoded
response — and it puts a content coding on the wire for no reason of the
client's. `no-transform` is what travels: the ingress, the CDN and every cache
on the path read it, where a content coding speaks only to whatever reads the
response next. `CompressedResponseTests` covers both, and the `no-transform`
one is red against the provider's registration removed.

**This too was written the wrong way round first**, and the correction is worth
keeping because the wrong version looked like a mitigation. It told the BFF to
*encode* the response itself. That protects nothing: gzip opens the same length
side channel wherever it is applied, so a BFF-compressed secret leaks exactly
as a gateway-compressed one does, and the pass-through test proves only that
the gateway declines to encode a second time. The header check is the same
mechanism either way; what changed is which value a downstream must send to be
safe. And a service that one day needs to
accept an upload meets §10.1's body ceiling first, which is a number in
`GatewayLimits` rather than a per-route setting: raising it is a platform
decision made once, in the open.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
