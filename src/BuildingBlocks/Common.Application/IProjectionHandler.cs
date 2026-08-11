namespace Common.Application;

/// <summary>
/// Reacts to this service's OWN events after commit, via the <c>Local</c>
/// outbox lane. Read-model projections and local cache invalidation (§8.4).
/// Never a public contract.
/// </summary>
/// <remarks>
/// One of three handler interfaces, and confusing them is the likeliest
/// mistake in this area — they differ by where the message came from (§9.4).
/// The other two arrive with PR-15's consumers:
/// <c>IIntegrationEventHandler&lt;T&gt;</c> for another service's event off
/// the broker, and <c>ICommandHandler&lt;,&gt;</c>, which already exists and
/// serves message-borne commands as well as HTTP ones.
/// <para>
/// Deliberately unconstrained. Every <see cref="Common.Domain.IDomainEvent"/>
/// carries <c>OccurredAt</c>, so a constraint would compile today; it would
/// also make §13.3's projection-lag metric the reason this could not accept a
/// plain read-model-shaped record tomorrow. The lag is read off the outbox
/// row instead, which already has the timestamp (§9.4).
/// </para>
/// </remarks>
/// <remarks>
/// <b>Invariant, and the <c>in</c> it does not carry is a decision.</b>
/// Contravariance would advertise that an <c>IProjectionHandler&lt;IDomainEvent&gt;</c>
/// handles every concrete event, and nothing in this design delivers on that:
/// the §6.2 scan registers each implementation under the exact interface it
/// implements, <c>IProjectionRegistry</c> asks for
/// <c>IProjectionHandler&lt;TConcrete&gt;</c>, and the built-in container does
/// no variance lookup — <c>GetServices</c> matches the closed type or nothing.
/// A broad handler would therefore be registered, invisible, and silent: the
/// registry finds nothing, no <c>Local</c> row is staged, and the projection
/// never runs while every dashboard stays green. That is the precise failure
/// §7.5's "empty is a decision" table exists to rule out, so the interface
/// states the exact-match semantics the container actually has.
/// </remarks>
public interface IProjectionHandler<TEvent>
{
    Task HandleAsync(TEvent domainEvent, CancellationToken ct);
}
