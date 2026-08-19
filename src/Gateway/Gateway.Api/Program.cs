using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Common.Web;
using Gateway.Api;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.DependencyInjection.Extensions;

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

// §10.1's request size limit. Kestrel's own 30 MB is a web server's default
// rather than a platform's choice, and this platform accepts JSON commands and
// nothing else — GatewayLimits argues the number.
//
// Enforced where the body is READ, which at the edge means inside the
// forwarder: an oversized request that fails authorization is answered 401 or
// 403 without its size ever being considered, because neither of those
// middlewares touches the body. Measured, not assumed. That ordering is the
// right way round — the cheaper refusal wins — but it does mean the ceiling
// only ever applies to requests the gateway was going to proxy anyway.
//
// And it bounds BYTES READ, not memory. Kestrel and YARP stream the body with
// backpressure, so an oversized request is never resident here; what the
// number caps is the bandwidth and forwarding work one caller can spend, which
// is the figure to reason about rather than a per-request allocation.
//
// Kestrel throws BadHttpRequestException(413) past it, which
// ExceptionHandlerMiddleware turns into §10.5's problem+json on its own: the
// status comes off the exception rather than from the 500 default, so this
// needs no handler of its own beside ValidationExceptionHandler and
// ConcurrencyExceptionHandler. Verified over both framings — a declared
// Content-Length and a chunked body with none — because only the first is
// refused before a byte of the body is read.
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = GatewayLimits.MaxRequestBodyBytes);

// §10.1's response compression, and the whole of ADR-020 is in the argument
// for that one property.
//
// EnableForHttps is false by default, against BREACH — not CRIME, which
// attacked TLS-level compression and is a different layer — and here it is
// what makes compression happen at all. TLS terminates at the ingress (§10.1)
// so the hop this host serves is plain http — but the forwarded-headers block
// below enables XForwardedProto, UseForwardedHeaders rewrites Request.Scheme
// from the ingress's header, and this middleware takes its decision at the
// first WRITE, below the whole pipeline, so the scheme it reads is the
// rewritten one. Registration order says nothing about it. Left at the
// default, a gateway behind an HTTPS ingress compresses nothing and says
// nothing about it. Measured: ForwardedSchemeCompressionTests goes red against
// the property removed.
//
// So the flag cannot be argued from the scheme in either direction, and
// ADR-020 argues it from content instead — no body crossing this edge pairs a
// secret with reflected input.
//
// The providers and the compressible MIME types are the framework's defaults,
// deliberately. Brotli and Gzip at CompressionLevel.Fastest is the right trade
// at an edge every response passes through, and the default type list omits
// application/problem+json — so §10.5's error bodies travel uncompressed, which
// is both the cheaper answer for a 250-byte body and the one place the platform
// reflects a client-supplied value (§10.4's correlation ID) back beside
// anything else. That omission is a default this code RELIES on without
// stating, so CompressedResponseTests pins it from the wire in both
// directions — a proxied application/json arrives encoded and a problem+json
// arrives plain — rather than leaving a framework list to change underneath an
// argument ADR-020 makes.
builder.Services.AddResponseCompression(o => o.EnableForHttps = true);

// RFC 9111's no-transform, which ASP.NET Core does not implement and which a
// reverse proxy may not ignore: the directive says an intermediary MUST NOT
// transform the content, and a content coding is such a transformation
// (RFC 9110 §7.7). Measured before this line existed — a body sent under the
// directive came back gzipped — so the gateway was violating it. This is also
// what makes ADR-020's opt-out the standard one rather than
// Content-Encoding: identity, which worked only as a side effect of the
// double-compression guard.
//
// Replace rather than registering ahead of AddResponseCompression: that call
// uses TryAddSingleton, so ordering would silently decide this, and a
// registration whose correctness depends on sitting above another line is the
// shape §10.3's own registration comment warns about.
builder.Services.Replace(
    ServiceDescriptor.Singleton<IResponseCompressionProvider, NoTransformResponseCompressionProvider>());

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
        {
            context.HttpContext.Response.Headers.RetryAfter =
                RetryAfterHeader.Seconds(retryAfter).ToString(CultureInfo.InvariantCulture);
        }

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

    // And each value has to be a browser origin, not merely a non-empty
    // string. `https//spa.example` — one missing colon — is accepted by
    // WithOrigins as a literal it will compare against and never match, so the
    // host starts healthy and every browser is refused: the same outcome as a
    // blank entry, reached through a typo rather than an absent variable. An
    // origin is scheme, host and optional port and nothing else, so a trailing
    // path is rejected too; a browser sends none, and one configured here
    // would silently match nothing.
    // One equality rather than a list of prohibitions, and the list is why.
    // Six review rounds added six clauses — blank, "*", unparseable, trailing
    // slash, path, credentials — and the seventh value found was
    // `https://spa.example:443`, which every one of them permits: a browser
    // serialises an origin without the scheme's default port, so WithOrigins
    // compares the configured text and never matches. Enumerating the ways a
    // string can fail to be an origin was losing to the ways a string can be.
    //
    // GetLeftPart(UriPartial.Authority) is the canonical origin — scheme, host
    // and a port only when it is not the default — so requiring the configured
    // text to equal it accepts exactly what a browser will send and rejects
    // every variant at once, including the five clauses this replaces.
    // UserInfo stays a separate test because the authority form keeps it.
    int[] malformed =
    [
        .. origins
            .Select((origin, index) => (origin, index))
            .Where(entry =>
                !Uri.TryCreate(entry.origin, UriKind.Absolute, out Uri? parsed) ||
                (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) ||
                parsed.UserInfo.Length > 0 ||
                !string.Equals(entry.origin, parsed.GetLeftPart(UriPartial.Authority), StringComparison.Ordinal))
            .Select(entry => entry.index)
    ];

    // Indexes, never the values. Credentials in the authority are one of the
    // shapes rejected above, so echoing the offending string would copy a
    // password into a startup exception — and an exception message reaches the
    // logs, where §13.4's redactor cannot help: it scrubs keyed attributes and
    // says in its own file that it cannot see a secret interpolated into a
    // message. The guard that rejects credentials must not be the thing that
    // publishes them. An operator holds the configuration and an index is
    // enough to find the entry.
    if (malformed.Length > 0)
    {
        throw new InvalidOperationException(
            $"'Cors:Origins' is not a browser origin at index {string.Join(", ", malformed)}. One is a scheme, " +
            "a host and a port only when it is not the scheme's default, exactly as a browser serialises it — " +
            "WithOrigins compares the configured text, so anything else matches nothing and the host would " +
            "start and refuse every browser (§15.4). The value is deliberately not echoed (§13.4).");
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

// High enough to wrap every writer below it — the proxy, the limiter's 429 and
// the status code pages — because this middleware compresses by replacing the
// response body feature, so it can only act on what runs inside it.
//
// There is no ordering rule here that a test could catch, and saying so is the
// honest version: moving this line below the limiter or the auth pair changes
// no observable response, since the only bodies those two produce are
// problem+json and the default MIME list does not compress it. What a test
// does catch is the line's absence — CompressedResponseTests goes red without
// it, which is the failure mode that matters: AddResponseCompression on its
// own succeeds and compresses nothing, the same shape §10.3's registration has.
app.UseResponseCompression();     // §10.1, ADR-020

// §10.5's promise applied to the statuses no handler produces. A challenge and
// a forbid are written by the middleware below before any endpoint runs, and
// they carry NO BODY — so the platform's "one error shape regardless of which
// service produced it" had two holes in it, on the two statuses a client meets
// first. AddProblemDetails registers a writer and nothing was calling it for
// these; since .NET 8 this middleware does, which is why it is one line rather
// than a handler. Above the auth middleware, because it converts what they
// write on the way back out.
app.UseStatusCodePages();         // §10.5 — 401 and 403 as problem+json

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
