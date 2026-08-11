namespace Common.Application;

/// <summary>
/// Maps an inbound command contract to its application command (§6.2). One
/// implementation per command contract a service accepts, discovered by the
/// §6.2 scan.
/// </summary>
/// <remarks>
/// <b>The mapping is explicit because the two types are not the same kind of
/// thing.</b> <c>TMessage</c> is a published contract owned by the service that
/// accepts it (§3.2) and versioned as a contract; <c>TCommand</c> is an
/// application type free to change with the code. Forwarding one as the other
/// would pin the command's shape as wire format — and the conversions are real:
/// <c>CancelOrder.Reason</c> is a string code that has to be parsed back into
/// <c>CancellationReason</c> (§9.6).
/// <para>
/// <b>The command carries no constraint here, and <c>CommandConsumer</c> is
/// where one appears.</b> Requiring <c>ICommand&lt;Result&gt;</c> on this
/// interface would be a second declaration of the consumer's own rule, in a
/// place that does not dispatch anything — and it is the consumer that has to
/// hand the result to <c>IDispatcher</c>.
/// </para>
/// <para>
/// Variance is inert for resolution and is declared anyway, on §6.2's terms:
/// the container matches the closed type either way, and <c>out TCommand</c>
/// says the truthful thing about a member that only ever returns one.
/// </para>
/// </remarks>
public interface ICommandMessageMapper<in TMessage, out TCommand>
    where TMessage : class
{
    /// <summary>
    /// Maps the message, or throws <see cref="ContractMappingException"/> for a
    /// value it cannot map.
    /// </summary>
    TCommand Map(TMessage message);
}
