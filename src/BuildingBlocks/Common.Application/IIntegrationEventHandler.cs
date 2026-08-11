namespace Common.Application;

/// <summary>
/// Reacts to an integration event published by ANOTHER service, delivered by
/// the broker and invoked by <c>IntegrationEventConsumer&lt;T&gt;</c> (§9.4)
/// behind the inbox filter (§9.5).
/// </summary>
/// <remarks>
/// <b>Not to be confused with <see cref="IProjectionHandler{TEvent}"/>.</b> The
/// two differ by where the message came from, which is the most likely mistake
/// in this area (§9.4): a projection handler reacts to <em>this</em> service's
/// own domain events on the outbox's <c>Local</c> lane after commit, and is
/// retried by the outbox's attempt counter. This one reacts to another
/// service's published contract and is retried by broker redelivery.
/// <para>
/// <b>Invariant, and the missing <c>in</c> is a decision.</b> Declaring it
/// contravariant would advertise that an
/// <c>IIntegrationEventHandler&lt;IIntegrationEvent&gt;</c> handles every
/// concrete contract, and nothing delivers on it: the §6.2 scan registers each
/// implementation under the exact interface it implements, the consumer asks
/// for the closed type, and the built-in container does no variance lookup.
/// A broad handler would be registered, invisible and silent.
/// </para>
/// </remarks>
public interface IIntegrationEventHandler<TEvent>
    where TEvent : class
{
    Task HandleAsync(TEvent integrationEvent, CancellationToken ct);
}
