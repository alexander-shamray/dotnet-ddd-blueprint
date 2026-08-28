namespace Common.Application;

/// <summary>
/// The command under this key has already committed, and its outcome is not
/// available to hand back. Nothing ran on <em>this</em> attempt — §6.3 refuses
/// before the handler — and the earlier one may well have run to completion;
/// the two paths are in the remarks.
/// </summary>
/// <remarks>
/// <b>This is the answer §8.5 owed and could not give.</b> Its release table
/// used to say the <c>catch</c> path releases "with one exception this code
/// cannot detect" — a commit that succeeded on the server whose acknowledgement
/// was lost. §6.3 now reads the durable marker at the top of the transaction,
/// so the retry that follows such a fault is refused here instead of writing
/// the aggregate a second time.
/// <para>
/// <b>Not a domain refusal, for
/// <see cref="ConcurrentRequestException"/>'s reason</b>: §10.5 maps an
/// <see cref="Error"/> because the domain decided something, and no handler
/// ran. It shares that exception's 409, because the statement is the same one
/// — this request conflicts with work already done — and the two are told apart
/// by their <c>Detail</c>, which is all a client needs.
/// </para>
/// <para>
/// <b>What the caller does not get is a replay, and there are two ways to
/// arrive here — only one of which is "no result was ever recorded".</b> The
/// first is the lost acknowledgement itself: the attempt threw before it
/// returned, so §8.5's store never saw an outcome. The second is ordinary
/// expiry, and it is the commoner one on the shipped defaults — the Redis entry
/// lives 24 hours and the marker a week, so a retry arriving on day three finds
/// the outcome *gone* rather than never written. The response says "cannot be
/// returned" rather than "was not recorded" for that reason.
/// </para>
/// <para>
/// <b>That second path is a deliberate change to what a late retry does, and it
/// is the price of the guarantee.</b> Before the marker, a retry after the
/// claim expired ran the command again — §8.5's opening sentence bounded its
/// promise to the retention for exactly that reason. It is now refused for as
/// long as the marker survives. A client that needs the outcome reads the
/// resource; what this exception protects is the half that cannot be recovered
/// afterwards, which is that the write did not happen twice.
/// </para>
/// </remarks>
public sealed class CommandAlreadyCommittedException(string key)
    : Exception("A command with this identifier has already been committed.")
{
    /// <summary>
    /// The idempotency key, for the log line and nothing else — it carries the
    /// subject segment (§8.5), so no response describes it.
    /// </summary>
    public string Key { get; } = key;
}
