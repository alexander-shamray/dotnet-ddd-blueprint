using Common.Application;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Common.Web;

/// <summary>
/// §8.5's durable answer, and the third producer of §10.5's 409 row.
/// <see cref="CommandAlreadyCommittedException"/> is raised by §6.3's
/// transaction when a command's key already carries a marker, so — exactly like
/// the 400 and the other two 409s — it is not a response until something
/// translates it, and <c>UseExceptionHandler</c>'s fallback answers 500.
/// </summary>
/// <remarks>
/// <b>Unregistered, this reports the mechanism working as a server fault, and
/// the cost is higher here than for its neighbour.</b>
/// <c>ConcurrentRequestExceptionHandler</c> covers a request told to wait; this
/// one covers a request told that its work is already done. A 500 invites
/// exactly the retry this exception exists to refuse — and a client that keeps
/// retrying meets the same 500 until the marker's retention expires, at which
/// point the command runs a second time. The missing registration would put the
/// duplicate write back, one release later.
/// <para>
/// <b>409 and not 200, which is the tempting answer.</b> The work committed, so
/// answering success reads as tidy — and there is nothing to answer with. On
/// the lost acknowledgement the attempt threw before returning, so §8.5's store
/// never recorded an outcome; on the commoner path it recorded one that has
/// since expired under a longer-lived marker. Either way the result is gone,
/// which is why the <c>Detail</c> below says <i>no longer available</i> rather
/// than naming a cause. A 200 with no body would be a success-shaped response
/// to a request whose result this service cannot produce, which is worse than
/// a conflict the client can act on.
/// </para>
/// <para>
/// <b>It is distinguishable from its neighbour only by <c>Detail</c>, and that
/// is deliberate</b> — the two say different things about what the client
/// should do next, and 409 is the statement they share. "Retry" is right for a
/// request that is merely in flight; it is wrong here, and the text says so.
/// </para>
/// </remarks>
internal sealed class CommandAlreadyCommittedExceptionHandler(IProblemDetailsService problemDetails)
    : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is not CommandAlreadyCommittedException)
            return false;

        httpContext.Response.StatusCode = StatusCodes.Status409Conflict;

        await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                // No key in the body. It carries the subject segment (§8.5),
                // which is a principal's identity, and no response describes
                // one — the same reason the neighbouring handler declines to
                // echo the CommandId.
                // "Cannot be returned" rather than "was not recorded", because
                // both are true and only the first is always true: the outcome
                // may never have been recorded (the lost acknowledgement), or
                // it may have been recorded and expired (§8.5's Redis entry
                // outlives the marker by nothing — the marker outlives IT by
                // days). A response that named the cause would be wrong on the
                // commoner path.
                Detail =
                    "This command has already been applied and its result is no longer " +
                    "available; read the resource rather than retrying.",
                // The discriminator that matters most on this status, because
                // this is the producer whose instruction is the opposite of the
                // other two. RFC 9457 makes `detail` human-readable, so a
                // client told apart from "retry" only by English prose is a
                // client that retries on a translation or a reword.
                Extensions = { ["code"] = "command.already_committed" }
            }
        });

        return true;
    }
}
