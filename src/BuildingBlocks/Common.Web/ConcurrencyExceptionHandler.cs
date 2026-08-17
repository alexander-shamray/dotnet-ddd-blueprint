using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Common.Web;

/// <summary>
/// The 409 row of §10.5's table, and an executor for it in the sense that
/// section already spells out for the 400: an exception is not a response
/// until something translates it, and <c>UseExceptionHandler</c>'s fallback
/// answers 500 — the wrong statement about a race the client can simply retry.
/// </summary>
/// <remarks>
/// <para>
/// 409 is deliberately not an <c>ErrorType</c> member, which is §10.5's own
/// argument rather than a choice made here: a concurrency conflict is produced
/// beside a handler rather than returned by one, and giving <c>Error</c> a
/// member for it would put two producers on one status. That is also why
/// <c>Rule</c> maps to 422 — a domain refusal is not a race.
/// </para>
/// <para>
/// The 412 half of that row is <em>not</em> here. It needs a precondition
/// filter reading <c>If-Match</c>, and nothing in the solution sends or reads
/// an ETag yet; a handler that answered 412 without one would be inventing a
/// conversation neither side is having. What distinguishes the two is whether
/// the client sent a precondition, and until it can, every conflict is the
/// no-precondition case.
/// </para>
/// </remarks>
internal sealed class ConcurrencyExceptionHandler(IProblemDetailsService problemDetails) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is not DbUpdateConcurrencyException)
            return false;

        httpContext.Response.StatusCode = StatusCodes.Status409Conflict;

        // Handled the moment it matched, on ValidationExceptionHandler's
        // terms: a client whose Accept header refuses problem+json still lost
        // a race, and reporting "unhandled" here would fall through to the 500
        // fallback and blame the service for it.
        await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                // No entity names, no row versions: the rowversion is a
                // storage detail (§7.3), and a client that retries needs to
                // know only that its copy was stale.
                Detail = "The resource was modified by another request. Re-read it and retry."
            }
        });

        return true;
    }
}
