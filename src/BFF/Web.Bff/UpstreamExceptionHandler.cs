using Grpc.Core;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Web.Bff;

/// <summary>
/// §9.7's third rule — "a fallback: cached data, a degraded response, or a
/// clear error, decided in advance rather than improvised during an incident".
/// This host's answer is the clear error, and this type is where it is decided.
/// </summary>
/// <remarks>
/// <b>Without it every upstream failure is a 500, which is a lie about whose
/// fault it is.</b> A <see cref="RpcException"/> escaping an endpoint reaches
/// the pipeline's fallback and is reported as the BFF having failed — so a
/// Catalog outage, a tripped circuit breaker and a genuine bug in this host all
/// arrive at the client, at the dashboards and at whoever is paged looking
/// identical.
/// <para>
/// The mapping is deliberately small. Only two of gRPC's statuses mean
/// something different to a caller here: a request this host built wrongly, and
/// an upstream that could not answer. Everything else keeps the 500, because a
/// status this code has not thought about is exactly the case where inventing a
/// friendlier answer would hide it.
/// </para>
/// </remarks>
internal sealed class UpstreamExceptionHandler(IProblemDetailsService problemDetails) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is not RpcException rpc)
            return false;

        (int status, string title) = rpc.StatusCode switch
        {
            // Catalog refused the request as malformed (§9.7's interceptor).
            // The BFF built that request from the caller's query string, so the
            // caller is who has to change something — 502 would send them to
            // read Catalog's logs for a mistake in their own URL.
            StatusCode.InvalidArgument => (StatusCodes.Status400BadRequest, "Invalid pricing request"),

            // The upstream is down, refusing connections, or the circuit
            // breaker is open — the last of which presents as Unavailable
            // without a call having left this process. 503 rather than 502,
            // because it is the status that says "try again later" and a
            // breaker's whole promise is that later is different.
            StatusCode.Unavailable or StatusCode.DeadlineExceeded =>
                (StatusCodes.Status503ServiceUnavailable, "Pricing is temporarily unavailable"),

            _ => (StatusCodes.Status500InternalServerError, "Pricing failed")
        };

        // An unmapped status is left to the pipeline's own fallback rather than
        // written here as a 500. The two produce the same status and NOT the
        // same telemetry: returning false leaves the exception unhandled, so it
        // is logged as one and reaches §13.2's error metrics, where writing it
        // here would quietly reclassify every unforeseen upstream fault as a
        // handled response.
        if (status == StatusCodes.Status500InternalServerError)
            return false;

        httpContext.Response.StatusCode = status;

        // The gRPC detail is deliberately not copied into the body. It is
        // another service's message, and §10.5's contract is that a client
        // reads one error shape from this platform rather than a passthrough of
        // whatever the hop behind it happened to say.
        await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = new ProblemDetails
            {
                Status = status,
                Title = title
            }
        });

        return true;
    }
}
