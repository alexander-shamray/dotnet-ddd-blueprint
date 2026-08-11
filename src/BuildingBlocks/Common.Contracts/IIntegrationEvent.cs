namespace Common.Contracts;

/// <summary>
/// Implemented by every integration event. No behaviour and no domain types —
/// three primitives, which is what keeps this legal under §9.1's rule that a
/// contract may not name a domain type.
/// </summary>
/// <remarks>
/// <b><see cref="MessageId"/> here is <em>the</em> message id, not a second
/// one.</b> The envelope's value is what <c>OutboxMessage.Stage</c> writes to
/// the row (§9.4), what the dispatcher puts on the transport, what
/// MassTransit's header carries, and therefore what the inbox will dedupe on
/// (§9.5). Body, row, header and inbox key are one GUID.
/// <para>
/// That has to be stated because the alternative is so easy to write and so
/// hard to see: a <c>Guid.CreateVersion7()</c> in <c>Stage</c> would compile,
/// work, and give every event two identities — one a consumer reads out of the
/// payload, one the broker and the inbox use. Nothing fails. The cost arrives
/// during an incident, when the id in the application log cannot be found in
/// the inbox table. <see cref="CorrelationId"/> follows the same rule for the
/// same reason: the mapper decides it (§9.3), because a business correlation
/// is more useful across a saga than an ambient request id.
/// </para>
/// </remarks>
public interface IIntegrationEvent
{
    Guid MessageId { get; }

    Guid CorrelationId { get; }

    DateTimeOffset OccurredAt { get; }
}
