using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Common.Web;

/// <summary>
/// The correlation ID every log line, message and trace is filtered by during
/// an incident (§10.4). Called by the gateway and by every service host, above
/// everything that logs — a log line written before it has no ID.
/// </summary>
public static class CorrelationIdExtensions
{
    /// <summary>§10.4's header, the one name the platform carries an ID under.</summary>
    /// <remarks>
    /// Public because it is read outside this file — by
    /// <see cref="CorrelationIdHandler"/> on the way out and by
    /// <c>AddCommonProblemDetails</c> when it builds §10.5's body. It was
    /// private while both of those spelled the literal instead, which is two
    /// copies of a contract in one assembly.
    /// <para>
    /// The tests deliberately keep their own literals. A contract test that
    /// reads the constant cannot notice the constant changing, which is the
    /// one thing it is there to notice.
    /// </para>
    /// </remarks>
    public const string Header = "X-Correlation-Id";

    /// <summary>
    /// The longest supplied ID this middleware will adopt (§10.4).
    /// </summary>
    /// <remarks>
    /// Both values the fallback mints are far shorter — a 32-character trace ID
    /// or a 36-character GUID — so the bound is generous rather than tight, and
    /// exists to stop an unauthenticated caller choosing how much of every log
    /// record on the platform it writes. Kestrel's own header budget is tens of
    /// kilobytes, and this middleware runs above <c>UseAuthentication</c>
    /// (§4.2), so the input is unauthenticated on every request that reaches a
    /// host.
    /// </remarks>
    public const int MaxSuppliedLength = 128;

    /// <summary>
    /// Assigns a correlation ID to any request that arrives without one, echoes
    /// it on the response, and pushes it onto the log scope.
    /// </summary>
    /// <remarks>
    /// Two middlewares sit above this: <c>UseSecurityHeaders</c> (§10.6), which
    /// decides nothing about correlation, and <c>UseExceptionHandler</c>. The
    /// second is deliberate, and it is why the ID is written onto the <em>request</em>
    /// rather than only into the log scope. An exception unwinding past here
    /// disposes the scope before the handler catches it, so the scope is gone
    /// by the time §10.5 builds the response — but <c>Request.Headers</c> is
    /// not, which is where <c>CustomizeProblemDetails</c> reads it from.
    /// </remarks>
    public static IApplicationBuilder UseCorrelationId(this IApplicationBuilder app)
    {
        // Resolved once, outside the delegate: this runs on every request, and
        // ILoggerFactory is a singleton whose per-request lookup buys nothing.
        ILogger logger = app.ApplicationServices
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("Common.Web.CorrelationId");

        return app.Use(async (context, next) =>
        {
            // FirstOrDefault on absent headers is null; an empty header value
            // is not, and would otherwise become a correlation ID of "".
            string? supplied = context.Request.Headers[Header].FirstOrDefault();

            string correlationId = IsAdoptable(supplied)
                ? supplied
                : Activity.Current?.TraceId.ToString() ?? Guid.CreateVersion7().ToString();

            context.Request.Headers[Header] = correlationId;

            // The RESPONSE header is written from OnStarting rather than here,
            // for §10.6's reason one middleware over: UseExceptionHandler
            // CLEARS the response before writing §10.5's problem body, so a
            // header assigned on the way in is gone from exactly the 500 an
            // incident is triaged from. The request header stays an eager
            // write — it is what CustomizeProblemDetails reads after the log
            // scope has been disposed, and nothing clears it.
            //
            // A static callback with the value passed as state, so the closure
            // captures nothing and this allocates once per request rather than
            // twice.
            context.Response.OnStarting(
                static state =>
                {
                    (HttpResponse response, string id) = ((HttpResponse, string))state;
                    response.Headers[Header] = id;

                    return Task.CompletedTask;
                },
                (context.Response, correlationId));

            // BeginScope, the Microsoft.Extensions.Logging primitive — not
            // Serilog's LogContext. OpenTelemetry is the whole logging stack here
            // (Appendix B), and it reads scopes; §13.3's LoggingBehavior uses the
            // same call for the same reason.
            //
            // CodeQL RAISES cs/log-forging HERE AND IT IS DISMISSED AS A FALSE
            // POSITIVE, recorded beside the code rather than only in a GitHub
            // field, because an accepted finding is a decision somebody has to
            // re-read. The value is not user-controlled by this point:
            // IsAdoptable below admits only [A-Za-z0-9_-] within a length
            // bound and anything refused is REPLACED rather than echoed, so
            // what reaches the scope is either a caller value from that
            // alphabet or one this host minted. The query does not read that
            // allow-list as a sanitiser. Kestrel refuses CR and LF inside a
            // request header value besides, so log splitting is not reachable
            // through this path at all — which the remarks below already say,
            // and which is why the alphabet check is a bound rather than a
            // rescue. Narrowing this would mean breaking §10.4's promise that
            // a caller's own trace ID survives the hop.
            using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
                await next();
        });
    }

    /// <summary>
    /// Whether a supplied header value is a plausible identifier this host is
    /// willing to adopt, rather than merely a non-blank string.
    /// </summary>
    /// <remarks>
    /// <b>Anything refused is replaced, never echoed.</b> The adopted value
    /// reaches four places — the response header, the forwarded request, the
    /// log scope every record for this request inherits, and §10.5's problem
    /// body — so a value that fails here would otherwise be reflected to an
    /// unauthenticated caller and multiplied into collector ingest by the
    /// record count.
    /// <para>
    /// The alphabet is the one both fallback branches already mint from: a
    /// 32-character hex trace ID and a dashed GUID. Underscore is admitted
    /// beside the hyphen because an upstream edge that mints its own IDs
    /// commonly uses it, and neither character can break a log line or a query.
    /// Deliberately <em>not</em> narrowed to exactly a trace ID or a GUID:
    /// §10.4's promise is that an ID chosen by the caller's own tracing
    /// survives the hop, and this platform is not the only thing that mints
    /// one.
    /// </para>
    /// <para>
    /// Kestrel already rejects CR and LF inside a request header value, so log
    /// splitting is not reachable through it — this is the bound on length and
    /// alphabet, not a rescue from that.
    /// </para>
    /// </remarks>
    private static bool IsAdoptable([NotNullWhen(true)] string? supplied)
    {
        if (supplied is not { Length: > 0 and <= MaxSuppliedLength })
            return false;

        foreach (char c in supplied)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_'))
                return false;
        }

        return true;
    }
}
