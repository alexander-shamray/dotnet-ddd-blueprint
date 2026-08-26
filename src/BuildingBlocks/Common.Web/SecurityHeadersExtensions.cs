using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Common.Web;

/// <summary>
/// §10.6's response security headers — the one this platform owns, and the
/// argument for the ones it deliberately does not set.
/// </summary>
/// <remarks>
/// <b>The service owns this, not the edge.</b> The gateway is one of four hosts
/// and is not in front of the other three from inside the cluster (§11.2
/// assumes the network is hostile), so a header set only there is absent on
/// every path that does not traverse it — including the BFF's own responses
/// during a direct call. Setting it in a building block every host composes is
/// the only placement that has no such gap.
/// <para>
/// <b><c>X-Content-Type-Options: nosniff</c> is the whole list, and the
/// omissions are decisions.</b> <c>Strict-Transport-Security</c> belongs to the
/// Ingress, which is the only component in this platform that terminates TLS
/// (§10.1, §15.3) — a host behind it sees plain HTTP and would be asserting
/// something it cannot observe. <c>X-Frame-Options</c> and
/// <c>Content-Security-Policy</c> govern how a browser renders a document, and
/// <b>no host here serves one</b>: the API responses are
/// <c>application/json</c> or <c>application/problem+json</c>, and §13.5's
/// probes are <c>text/plain</c> — measured, because an earlier draft of this
/// paragraph said every response was JSON and the health endpoints use the
/// framework's default plain-text writer. A framing or script policy on a body
/// no browser renders as a document protects nothing, and would have to be
/// revisited by whoever serves the storefront §4.1 plans. <c>nosniff</c> is the one that is not about
/// rendering: it stops a browser reclassifying a JSON response — including one
/// whose body is a value a caller supplied — as HTML and executing it.
/// </para>
/// <para>
/// <b>Written from <c>OnStarting</c> rather than before <c>next</c>.</b>
/// <c>UseExceptionHandler</c> clears the response before it writes §10.5's
/// problem body, so a header assigned on the way in is gone from exactly the
/// 500 that a caller-supplied value is most likely to be reflected on. A
/// callback registered here fires when the response actually starts, which is
/// after that clear.
/// </para>
/// </remarks>
public static class SecurityHeadersExtensions
{
    private const string ContentTypeOptions = "X-Content-Type-Options";
    private const string NoSniff = "nosniff";

    /// <summary>
    /// Adds <c>X-Content-Type-Options: nosniff</c> to every response (§10.6).
    /// Called outermost, above <c>UseExceptionHandler</c> (§4.2).
    /// </summary>
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // The RequestDelegate overload, not the Func<Task> one. The
        // parameterless spelling reads better and allocates a closure and a
        // wrapper per request, which contradicts ADR-031's own claim that this
        // middleware adds nothing per request — on a middleware every request
        // traverses, outermost.
        return app.Use((HttpContext context, RequestDelegate next) =>
        {
            // A static callback with the response passed as state: the closure
            // would otherwise capture `context` and allocate once per request.
            context.Response.OnStarting(
                static state =>
                {
                    HttpResponse response = (HttpResponse)state;

                    // Indexer rather than Append: a host or a proxy that has
                    // already set it must not end up with the header twice,
                    // which some browsers treat as no header at all.
                    response.Headers[ContentTypeOptions] = NoSniff;

                    return Task.CompletedTask;
                },
                context.Response);

            return next(context);
        });
    }
}
