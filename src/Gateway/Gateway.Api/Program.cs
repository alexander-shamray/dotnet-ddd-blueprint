using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Common.Web;
using Gateway.Api;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;

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

// YARP is registered here and configured from the "ReverseProxy" section of
// appsettings.json (§10.2). Without this, MapReverseProxy() throws at startup
// and the entire routing configuration is inert.
builder.Services
    .AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

// §10.3. Registration without middleware is the quiet failure mode: this call
// succeeds and does nothing at all if UseRateLimiter is missing below.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.AddPolicy(
        GatewayRateLimiterPolicies.Anonymous,
        context => RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 100,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));

    // The subject claim, which is empty until UseAuthentication has run — see
    // the pipeline below. The address fallback is for the genuinely anonymous
    // request that still matches an authenticated route, not a safety net for
    // pipeline order: with the order wrong it absorbs every request and this
    // policy degrades to a second copy of the one above with a bigger budget.
    options.AddPolicy(
        GatewayRateLimiterPolicies.Authenticated,
        context => RateLimitPartition.GetTokenBucketLimiter(
            partitionKey: context.User.FindFirstValue(ClaimTypes.NameIdentifier) ??
                context.Connection.RemoteIpAddress?.ToString() ??
                "unknown",
            factory: _ => new TokenBucketRateLimiterOptions
            {
                TokenLimit = 300,
                TokensPerPeriod = 300,
                ReplenishmentPeriod = TimeSpan.FromMinutes(1),
                QueueLimit = 10,
                AutoReplenishment = true
            }));

    // Through IProblemDetailsService rather than WriteAsJsonAsync, so a 429
    // is the same shape as every other error the platform returns (§10.5):
    // application/problem+json, with the correlationId and traceId members
    // AddCommonProblemDetails adds. Writing the body directly — which §10.3
    // printed until this landed — produces application/json and none of the
    // three, so the one response a client is most likely to handle
    // programmatically would be the one that did not match the contract.
    options.OnRejected = async (context, _) =>
    {
        // RetryAfterHeader.Seconds, not a cast — it rounds up, and that file
        // argues why the rule earns a type of its own rather than living here
        // where nothing can reach the case it exists for.
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retryAfter))
            context.HttpContext.Response.Headers.RetryAfter =
                RetryAfterHeader.Seconds(retryAfter).ToString(CultureInfo.InvariantCulture);

        // Set before writing: the customisation reads the response status to
        // fill in the RFC 9457 title and type when they are absent, and the
        // service refuses to write once the response has started.
        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;

        IProblemDetailsService problems = context.HttpContext.RequestServices
            .GetRequiredService<IProblemDetailsService>();

        await problems.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = context.HttpContext,
            ProblemDetails =
            {
                Status = StatusCodes.Status429TooManyRequests,
                Title = "Too many requests",
                Type = "https://tools.ietf.org/html/rfc6585#section-4"
            }
        });
    };
});

// Every policy §10.2's routes name that Common.Web does not already register.
// "authenticated" comes from AddCommonWebDefaults; this one is the gateway's
// own, and it is a permission check rather than a role check for the reason
// §11.4 gives.
//
// A route naming a policy nobody registered fails CLOSED, and loudly: at the
// pinned YARP the config load throws out of MapReverseProxy() below, naming
// the policy and the route, so the gateway does not start. Four sites in the
// blueprint described a silent per-route drop instead and were amended in this
// change — UnresolvablePolicyTests is where that was measured.
builder.Services
    .AddAuthorizationBuilder()
    .AddPolicy(GatewayPermissions.InventoryAdmin, p => p.RequirePermission(GatewayPermissions.InventoryAdmin));

// Both of the following are conditional on the deployment shape, and each is
// REQUIRED once switched on. "Off" and "on but unconfigured" are different
// states: the first is a valid topology, the second is a silent defect.
bool behindProxy = builder.Configuration.GetValue<bool>("Ingress:Enabled");
bool corsEnabled = builder.Configuration.GetValue<bool>("Cors:Enabled");

if (behindProxy)
{
    // A load balancer or Ingress sits in front (§15.3), so
    // Connection.RemoteIpAddress is the proxy on every request. Without this
    // the rate limiter partitions all anonymous traffic into ONE bucket and its
    // per-client limit becomes a global cap — configured, running, and a denial
    // of service against legitimate users rather than a defence.
    //
    // Read HERE and not inside the Configure callback below. An options
    // callback runs when the options are first resolved, so a missing section
    // read from inside one throws on a request rather than at startup — which
    // is exactly the deferral this pair of flags exists to avoid.
    string[] trusted = builder.Configuration.GetRequiredSection("Ingress:TrustedNetworks").Get<string[]>()!;

    builder.Services.Configure<ForwardedHeadersOptions>(o =>
    {
        o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

        // Trust only the ingress. Left empty, ASP.NET Core trusts nothing
        // beyond loopback and silently keeps the proxy's address; opened to
        // all, any client can spoof its partition key and bypass the limit.
        //
        // KnownIPNetworks, not KnownNetworks, and System.Net.IPNetwork rather
        // than the bare name. §4.2's sample said both the other way and did not
        // compile at this pin: KnownNetworks carries ASPDEPR005 in .NET 10 —
        // an error under ADR-019, not a warning to read past — and the
        // IPNetwork it holds is Microsoft.AspNetCore.HttpOverrides' own, which
        // the namespace imported above for the ForwardedHeaders flags brings
        // into scope in place of the framework type the new property takes.
        // The chapter was amended in this change.
        o.KnownIPNetworks.Clear();
        o.KnownProxies.Clear();

        foreach (string cidr in trusted)
            o.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(cidr));
    });
}

// Only when browsers call the gateway directly rather than through a CDN or
// same-origin edge (§10.2). Enabled but unset would yield WithOrigins([]),
// which rejects every browser request while starting cleanly — surfacing as a
// CORS error in a console rather than as the missing setting it is (§15.4).
// Hoisted out of the AddCors callback for the reason given above it: the CORS
// options are built on the first request that needs them, so the throw would
// otherwise arrive far from the deployment that caused it.
if (corsEnabled)
{
    string[] origins = builder.Configuration.GetRequiredSection("Cors:Origins").Get<string[]>() ?? [];

    // GetRequiredSection proves the section exists and nothing more, which is
    // half a guard. `Cors__Origins__0=` — the commonest way a deployment gets
    // this wrong — binds to an array holding one empty string: WithOrigins
    // accepts it, the host starts, and every browser request is rejected by a
    // policy that matches no origin at all. That is precisely the state this
    // flag pair exists to refuse, arriving through the one shape the section
    // check cannot see.
    //
    // The same finding AddJwtAuthentication already carries for
    // Identity:Authority (§11.3): blank counts as missing. It was learned once
    // there and not applied here until Copilot said so.
    if (origins.Length == 0 || origins.Any(string.IsNullOrWhiteSpace))
    {
        throw new InvalidOperationException(
            "'Cors:Origins' is enabled but holds no usable origin. An empty or blank entry yields a policy " +
            "matching nothing, so every browser request fails while the host reports healthy (§15.4).");
    }

    // And "*" separately, because it fails for the opposite reason: the policy
    // below calls AllowCredentials(), and ASP.NET Core refuses that pairing
    // when it BUILDS the options — which is the first request needing a CORS
    // policy, not startup. A wildcard is also the one value a reader would
    // assume is the permissive fallback, so it has to be refused where the
    // refusal is legible rather than in a stack trace on somebody's preflight.
    if (origins.Any(o => o == "*"))
    {
        throw new InvalidOperationException(
            "'Cors:Origins' contains '*', which cannot be combined with AllowCredentials — ASP.NET Core " +
            "throws when the policy is built, on the first preflight rather than at startup. Name the " +
            "origins, or drop credentials as a deliberate separate decision (§10.2).");
    }

    builder.Services
        .AddCors(o =>
            o.AddDefaultPolicy(p => p
                .WithOrigins(origins)
                .AllowAnyHeader()
                .AllowAnyMethod()
                .AllowCredentials()));
}

WebApplication app = builder.Build();

// Middleware order is behaviour, not formatting (§4.2).
app.UseExceptionHandler();        // §10.5 — outermost, catching middleware faults
app.UseCorrelationId();           // §10.4 — assigns the ID if the client sent none

// Before everything that reads the client address, and after the two that do
// not. §4.2's sample put this line first and PR-17 followed it; the chapter
// was amended in this change, because "first" cost both of the rules above it
// and bought nothing. A fault thrown parsing a forwarded header skipped the
// problem-details handler entirely, and anything this middleware logged ran
// outside the correlation scope — the one request a reader would then be
// unable to follow. Neither of those two reads RemoteIpAddress, so nothing
// about the rewrite is lost by letting them wrap it.
//
// Skipped when the gateway IS the edge (Compose), where RemoteIpAddress is
// already the client and trusting a forwarded header would let any caller
// choose its own rate-limit bucket. ForwardedHeadersTests holds the half that
// still matters: moved below UseRateLimiter, two forwarded addresses collapse
// onto the one connection the gateway can see and the second client is metered
// as the first client's hundred-and-second request. Observed red there.
if (behindProxy)
    app.UseForwardedHeaders();

if (corsEnabled)
    app.UseCors();

// Authentication FIRST, then the limiter, then authorization. §10.3's
// "authenticated" policy partitions on the subject claim, and until this line
// runs HttpContext.User is an empty principal — which does not fail, it just
// meters every signed-in caller behind one NAT as a single client.
app.UseAuthentication();          // §11.3
app.UseRateLimiter();             // §10.3 — needs the user, precedes policy work
app.UseAuthorization();           // §11.4

app.MapReverseProxy();
app.MapCommonHealthEndpoints();   // §13.5 — anonymous; kubelet carries no token

app.Run();

// Top-level statements compile to an INTERNAL Program, which
// WebApplicationFactory<Program> cannot see from another assembly (§12.4).
public partial class Program;
