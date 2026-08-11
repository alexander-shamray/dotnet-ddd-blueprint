using Common.Domain;

namespace Common.Application;

/// <summary>
/// §9.3's allow-list: the domain event types this service translates into
/// public contracts, and how. Everything unregistered is local-only.
/// </summary>
/// <remarks>
/// The port is common because <c>DomainEventDispatcher</c> injects it; the
/// implementation is per-service and lives in
/// <c>&lt;Service&gt;.Application.Integration</c>, because the allow-list is
/// a statement about one service's public surface.
/// <para>
/// The two failure semantics are deliberately different, and the distinction
/// is the whole point. A domain event <b>absent</b> from the registry is
/// skipped silently — most domain events are internal, and failing on them
/// would force every new event to be published or explicitly suppressed. A
/// registered mapper that <b>throws</b> fails the command and rolls the
/// transaction back: someone declared this event must be published, so if it
/// cannot be, the state change must not stand either.
/// </para>
/// <para>
/// There is deliberately no <c>MustPublish</c> flag on domain events. If it
/// must reach the bus, register it — one mechanism, one place to look.
/// </para>
/// </remarks>
public interface IIntegrationEventMapper
{
    IReadOnlyList<object> Map(IReadOnlyList<IDomainEvent> domainEvents);
}
