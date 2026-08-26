namespace Common.Application;

/// <summary>
/// Stages a message for delivery after the current transaction commits.
/// </summary>
/// <remarks>
/// <b>Called by <c>DomainEventDispatcher</c>, never by a command handler.</b>
/// The dispatcher runs at the single point where every aggregate has finished
/// changing; a handler that stages earlier serialises a snapshot the rest of
/// the handler can still move on from, and the payload is written here rather
/// than at commit — so a total adjusted two lines later commits an outbox row
/// that disagrees with the row beside it (§9.3).
/// <para>
/// The implementation <b>must</b> write on the same <c>DbContext</c> the
/// command handler is using, so the row enlists in the same transaction. It
/// <b>must not</b> call the broker transport directly — <c>IBus.Publish</c>
/// inside a handler reintroduces the dual write this exists to eliminate —
/// and it must not introduce a second outbox table set beside the existing
/// one, which would mean two dispatchers, two retention policies, two sets of
/// ordering guarantees, and one of them being the one nobody monitors.
/// </para>
/// <para>
/// One exemption, and it is Infrastructure's: a MassTransit state machine
/// (§9.6) sends and publishes from its activities, on its own receive
/// endpoint's transactional outbox rather than through this port. That
/// exemption is recorded as ADR-032 and it does cost a second table set — the
/// paragraph above is the price it was weighed against, not a rule it slipped
/// past. What makes it unavoidable is that a saga's timeouts are scheduled
/// messages, and a delay is a transport feature (ADR-021) that no dispatcher
/// of ours can replay: staging them here is not the cheaper option, it is not
/// an option. The prohibition applies to Application code, which is where the
/// dual-write risk actually lives.
/// </para>
/// </remarks>
public interface IIntegrationEventPublisher
{
    Task StageAsync(object message, OutboxLane lane, CancellationToken ct);
}
