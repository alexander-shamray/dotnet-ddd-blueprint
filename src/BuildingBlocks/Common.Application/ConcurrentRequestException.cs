namespace Common.Application;

/// <summary>
/// A second request arrived under a key whose first attempt has not finished.
/// </summary>
/// <remarks>
/// <b>Not a domain refusal, which is why it is an exception and not an
/// <see cref="Error"/>.</b> §10.5 maps an <c>Error</c> onto a status code
/// because the domain decided something; nothing has decided anything here. The
/// caller is being told to retry, and the retry may still succeed or fail on
/// its merits.
/// <para>
/// It is also raised for an entry the store has, but whose outcome it does not
/// — an attempt that claimed the key and then failed to record what happened.
/// That entry stays in progress until the retention expires, so this exception
/// is what every retry meets until then. §8.5's release table argues why
/// holding is still the right answer there, and the reason moved with
/// ADR-037: it postpones the replay of an outcome that was never stored, where
/// it used to postpone the duplicate. A release would no longer permit the
/// second commit — <see cref="CommandAlreadyCommittedException"/> is what
/// refuses that now.
/// </para>
/// <para>
/// <b>So the two options differ in how many answers the caller gets, and
/// holding is the one that gives more.</b> Held, a retry meets this exception
/// until the claim expires and
/// <see cref="CommandAlreadyCommittedException"/> after it — two answers over
/// time. Released, the next retry claims the free key, meets the marker inside
/// §6.3's transaction and gets that second answer straight away. What holding
/// buys is not fewer refusals but a claim that is never handed on while the
/// outcome of committed work is unknown; what it costs is up to a retention of
/// the less precise of the two answers.
/// </para>
/// </remarks>
public sealed class ConcurrentRequestException(Guid commandId)
    : Exception($"A request is already in progress for command {commandId}.")
{
    /// <summary>The contended <c>CommandId</c>, for the log line and nothing else.</summary>
    public Guid CommandId { get; } = commandId;
}
