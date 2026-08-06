using System.Diagnostics;
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
    private const string Header = "X-Correlation-Id";

    /// <summary>
    /// Assigns a correlation ID to any request that arrives without one, echoes
    /// it on the response, and pushes it onto the log scope.
    /// </summary>
    /// <remarks>
    /// One middleware sits above this: <c>UseExceptionHandler</c>. That is
    /// deliberate, and it is why the ID is written onto the <em>request</em>
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
            // FirstOrDefault on absent headers is null; an empty header value is
            // not, and would otherwise become a correlation ID of "".
            string? supplied = context.Request.Headers[Header].FirstOrDefault();

            string correlationId = string.IsNullOrWhiteSpace(supplied)
                ? Activity.Current?.TraceId.ToString() ?? Guid.CreateVersion7().ToString()
                : supplied;

            context.Request.Headers[Header] = correlationId;
            context.Response.Headers[Header] = correlationId;

            // BeginScope, the Microsoft.Extensions.Logging primitive — not
            // Serilog's LogContext. OpenTelemetry is the whole logging stack here
            // (Appendix B), and it reads scopes; §13.3's LoggingBehavior uses the
            // same call for the same reason.
            using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
                await next();
        });
    }
}
