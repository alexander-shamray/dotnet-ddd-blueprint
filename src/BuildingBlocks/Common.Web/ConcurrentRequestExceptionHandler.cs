using Common.Application;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Common.Web;

/// <summary>
/// §8.5's contention answer, and the second producer of §10.5's 409 row.
/// <see cref="ConcurrentRequestException"/> is raised beside the handler by
/// <c>IdempotencyBehavior</c>, so — exactly like the 400 and the other 409 —
/// it is not a response until something translates it, and
/// <c>UseExceptionHandler</c>'s fallback answers 500.
/// </summary>
/// <remarks>
/// <b>Unregistered, this reports a retryable wait as a server fault.</b> A
/// second request under a key whose first attempt is still running is the
/// mechanism working, not failing: nothing has gone wrong and nothing has been
/// decided. A 500 tells the client the opposite, and a client that treats 500
/// as fatal abandons an operation that was about to succeed.
/// <para>
/// <b>409 and not 425 or 503.</b> `Too Early` is about replayed TLS early
/// data, and 503 says the service is unavailable when it is serving every
/// other caller normally. The statement being made is the one 409 already
/// makes here — this request conflicts with another — which is why it shares
/// the row with <c>ConcurrencyExceptionHandler</c> rather than inventing a
/// status. The two are distinguishable by their <c>Detail</c>, and a client
/// needs no more than that: both say retry.
/// </para>
/// <para>
/// <b>The in-progress entry may also be one that never completes</b>, and the
/// text says "retry" rather than "wait" for that reason. §8.5's release table
/// has the case: an attempt that claimed the key and then failed to record its
/// outcome holds it until the retention expires, so every retry until then
/// meets this. Promising the caller a short wait would be a promise this code
/// cannot keep.
/// </para>
/// </remarks>
internal sealed class ConcurrentRequestExceptionHandler(IProblemDetailsService problemDetails) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is not ConcurrentRequestException)
            return false;

        httpContext.Response.StatusCode = StatusCodes.Status409Conflict;

        await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                // No CommandId in the body. It is the caller's own value, so
                // echoing it tells them nothing they did not send, and the key
                // it forms carries the subject segment (§8.5) — a detail no
                // response should be describing.
                Detail = "A request with this command identifier is already in progress. Retry."
            }
        });

        return true;
    }
}
