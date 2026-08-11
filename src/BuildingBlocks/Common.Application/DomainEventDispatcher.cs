using Common.Domain;

namespace Common.Application;

/// <summary>
/// §7.5's dispatcher, and the single normative description of how a domain
/// event becomes an outbox row: collect → map through the §9.3 allow-list →
/// stage <c>Broker</c> and <c>Local</c> rows. <b>It performs no I/O beyond
/// staging and invokes no handler</b>, which is the change that makes the rest
/// of the design safe (ADR-018).
/// </summary>
internal sealed class DomainEventDispatcher(
    IDomainEventCollector collector,
    IIntegrationEventMapper mapper,
    IIntegrationEventPublisher publisher,
    IProjectionRegistry projections)
    : IDomainEventDispatcher
{
    public async Task DispatchAsync(CancellationToken ct)
    {
        IReadOnlyList<IDomainEvent> events = collector.CollectAndClear();
        if (events.Count == 0)
            return;

        // Broker lane: allow-listed events become integration events (§9.3).
        foreach (object integrationEvent in mapper.Map(events))
            await publisher.StageAsync(integrationEvent, OutboxLane.Broker, ct);

        // Local lane: events with a registered projection handler are staged
        // too, so the projection survives a crash immediately after commit.
        // Only those — §9.4 throws on a Local row that finds no handler, and
        // the two checks read the same container so the pair cannot disagree.
        foreach (IDomainEvent domainEvent in events.Where(projections.HasHandler))
            await publisher.StageAsync(domainEvent, OutboxLane.Local, ct);
    }
}
