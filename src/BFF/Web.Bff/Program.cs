using Catalog.Pricing.V1;
using Common.Web;
using Microsoft.Extensions.Http.Resilience;
using Polly;
using Web.Bff;
using Web.Bff.Endpoints;
using Web.Bff.Identity;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Refuse to start if any registered service has a dependency the container
// cannot satisfy, or if a singleton captures a scoped one. Both are otherwise
// discovered on the first request that happens to need them.
builder.Host.UseDefaultServiceProvider(o =>
{
    o.ValidateOnBuild = true;
    o.ValidateScopes = true;
});

builder.AddCommonWebDefaults();                 // §13.2

// The BFF's own error translation, beside the two AddCommonProblemDetails
// already registers. It is here rather than in Common.Web because it is about
// an outbound call, and this is the only host that makes one (§9.7).
builder.Services.AddExceptionHandler<UpstreamExceptionHandler>();

// §9.7, §11.5 — the whole of the platform's client-credentials mechanism, in
// the one host that has any. Every line below is absent from every other
// Program.cs in the solution, and that is the design rather than an omission:
// "the gateway needs no client credentials" is true by CONSTRUCTION, because
// the gateway calls none of this and therefore neither binds Identity:Client
// nor demands it (§15.4).
builder.Services.AddTransient<ClientCredentialsHandler>();
builder.Services.AddSingleton<ITokenCache, CachingTokenClient>();

// The clock CachingTokenClient measures expiry against. Registered rather than
// read off DateTimeOffset.UtcNow so a test can hold a token past its lifetime
// without waiting for one — the same registration Catalog.Application makes
// for the same reason (§5.4).
builder.Services.AddSingleton(TimeProvider.System);

// Bound, validated and validated AT START. IOptions<T> always resolves —
// unbound it hands back a default-constructed instance — so a forgotten
// binding is invisible to ValidateOnBuild, the host starts clean, and the
// failure surfaces as 401s from Catalog that this host would report as
// Catalog's fault (§15.4).
builder.Services
    .AddOptions<ServiceIdentityOptions>()
    .BindConfiguration(ServiceIdentityOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();

// The token client's own transport, and it deliberately carries NO
// ClientCredentialsHandler: a client that attached a token in order to fetch a
// token would recurse until the stack ran out. The base address is the same
// authority §11.3 validates inbound tokens against — read through Common.Web's
// constant so the two cannot drift to different realms — and the trailing
// slash is load-bearing, because a relative ".well-known/..." against a base
// with no trailing slash replaces the last path segment and asks the wrong
// realm.
string authority = builder.Configuration[AuthenticationExtensions.AuthorityKey]!;

builder.Services
    .AddHttpClient(CachingTokenClient.HttpClientName, client =>
        client.BaseAddress = new Uri(authority.TrimEnd('/') + "/"));

// Three statements rather than §9.7's one fluent chain, and the chapter was
// amended in this change. AddStandardResilienceHandler returns an
// IHttpStandardResiliencePipelineBuilder — a different type, scoped to the
// pipeline it just registered — so the printed
// `.AddStandardResilienceHandler(…).AddHttpMessageHandler<T>()` does not
// compile at all: CS1929, found the only way it could be. Holding the
// IHttpClientBuilder in a local keeps both calls on the same receiver, and
// keeps the ORDER, which is the part that carries meaning.
IHttpClientBuilder pricing = builder.Services
    .AddGrpcClient<Pricing.PricingClient>(PricingHop.ClientName, o => o.Address = PricingHop.Address);

// Resilience is registered FIRST so that it sits OUTERMOST, and the credential
// handler runs inside it. That ordering matters: the handler then runs once
// per ATTEMPT rather than once per request, so a retried attempt asks the token
// cache again instead of replaying the first attempt's token.
//
// Usually it gets the same token back — CachingTokenClient serves one until its
// expiry guard — and that is the point of the cache rather than a hole in this.
// What the position buys is the case where the token expired mid-request.
//
// The retries that fire are transport faults — a gRPC status rides an HTTP 200
// and this pipeline never sees it (§9.7, UpstreamRetryTests) — so this is
// narrower than "recovers an expired token", which is how the comment read
// until a review pointed out that the failure it described cannot trigger a
// retry at all.
pricing
    .AddStandardResilienceHandler(options =>
    {
        // Outermost bound. Defaults to 30 s, which would breach the hierarchy
        // against ServiceOptions.OperationTimeout — equal is not below it.
        options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(5);

        options.Retry.MaxRetryAttempts = 2;            // 3 attempts in total
        options.Retry.BackoffType = DelayBackoffType.Exponential;
        options.Retry.UseJitter = true;
        options.Retry.Delay = TimeSpan.FromMilliseconds(150);

        // The cap that makes the budget below ARITHMETIC rather than
        // statistical, and without it the stated sum is not a bound at all.
        // UseJitter randomises each delay, and Polly's decorrelated jitter can
        // exceed the nominal for a single retry — measured at 392 ms against a
        // 300 ms nominal over 400 samples. The observed worst TOTAL stayed
        // under the un-jittered 450 ms, but a sample is not a bound, and the
        // strategy documents none without this.
        //
        // Capped, the worst case is 2 × 300 ms whatever the jitter draws, so
        // 3 × 1.4 s + 0.6 s = 4.8 s fits inside the 5 s total with the last
        // attempt able to finish. ResilienceHierarchyTests computes it from
        // this property rather than from the nominal.
        options.Retry.MaxDelay = TimeSpan.FromMilliseconds(300);

        // 3 × 1.4 s + 2 × 300 ms = 4.8 s. The delays are part of the budget
        // rather than an extra on top of it: leave them out and the arithmetic
        // clears the ceiling while the configuration does not, so the third
        // attempt is cancelled part-way and the retry that was meant to save
        // the request never had a chance. ResilienceHierarchyTests asserts the
        // sum including backoff for exactly that reason, and takes the backoff
        // from MaxDelay above rather than from the nominal.
        options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(1.4);

        options.CircuitBreaker.FailureRatio = 0.5;
        options.CircuitBreaker.MinimumThroughput = 10;
        options.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(15);

        // The breaker samples over a window, and the window must be at least
        // twice the attempt timeout or the library refuses the options at
        // startup. Left at its 30 s default it also outlives the break
        // duration, which is the shape that matters: a sampling window shorter
        // than the break would forget every failure while the circuit was open
        // and reopen it on the first fresh error.
    });

// Registered AFTER resilience, so it sits INSIDE it (§11.5). This one line is
// the whole reason the ordering comment above is worth reading.
pricing.AddHttpMessageHandler<ClientCredentialsHandler>();

// §10.4's outbound half, and the platform's only place for it: this is the one
// synchronous hop (§9.7, ADR-017), so it is the one call that could carry an ID
// across a process boundary and was not. Events already do — §9.1's envelope
// has the member — so the gap was exactly this edge.
//
// Inside the pipeline like the handler above, though for a weaker reason: the
// value does not change between attempts, so the position is uniformity rather
// than correctness. Outside it would work too.
builder.Services.AddTransient<CorrelationIdHandler>();
pricing.AddHttpMessageHandler<CorrelationIdHandler>();

WebApplication app = builder.Build();

// Middleware order is behaviour, not formatting (§4.2). No forwarded headers,
// no CORS and no rate limiter: all three are the edge's, and §15.4 marks their
// keys gateway-only. The BFF is behind that edge.
app.UseExceptionHandler();        // §10.5 — outermost, catching middleware faults
app.UseCorrelationId();           // §10.4 — above everything else that logs

// §10.5's promise applied to the statuses no handler produces: a challenge and
// a forbid are written by the middleware below and carry no body.
app.UseStatusCodePages();         // §10.5 — 401 and 403 as problem+json
app.UseAuthentication();          // §11.3 — populates HttpContext.User
app.UseAuthorization();           // §11.4

// No readiness check is registered anywhere in this host, so /health/ready
// reports ready immediately — which §13.5 says is correct for exactly two
// hosts, the gateway and this one, because neither owns a database. The rule
// that separates that from "readiness was never wired up" is whether the host
// has a connection string, and this one has none.
app.MapCommonHealthEndpoints();   // §13.5 — anonymous; kubelet carries no token
app.MapCheckoutEndpoints();

app.Run();

// Top-level statements compile to an INTERNAL Program, which
// WebApplicationFactory<Program> cannot see from another assembly (§12.4).
public partial class Program;
