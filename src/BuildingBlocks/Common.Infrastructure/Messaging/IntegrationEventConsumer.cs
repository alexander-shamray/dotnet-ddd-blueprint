using Common.Application;
using Common.Contracts;
using MassTransit;

namespace Common.Infrastructure.Messaging;

/// <summary>
/// §9.4's bridge from the broker to <see cref="IIntegrationEventHandler{TEvent}"/>.
/// With <see cref="CommandConsumer{TMessage, TCommand}"/> beside it, this is the
/// only place a MassTransit type meets application code — which is what ADR-014
/// depends on.
/// </summary>
/// <remarks>
/// <b>Common, not per-service.</b> §9.4 writes
/// <c>namespace Ordering.Infrastructure.Messaging</c> above this class for the
/// same reason it wrote <c>ordering.OutboxMessages</c> into the dispatcher: the
/// chapter is Ordering's viewpoint. Nothing here is per-service, and the
/// per-service half — which endpoint binds which contract — lives in each
/// service's <c>AddMassTransitMessaging</c>, where §9.8 configures it.
/// </remarks>
public sealed class IntegrationEventConsumer<TEvent>(
    IEnumerable<IIntegrationEventHandler<TEvent>> handlers,
    MessagingMetrics metrics,
    TimeProvider clock)
    : IConsumer<TEvent>
    where TEvent : class, IIntegrationEvent
{
    public async Task Consume(ConsumeContext<TEvent> context)
    {
        // Publish-to-consume lag, read straight off the message (§13.3). The
        // IIntegrationEvent constraint is what makes OccurredAt reachable here
        // — without it this method sees only an object-shaped generic, which is
        // why the Local lane's twin has to read its timestamp off the row.
        //
        // Recorded before the handlers, unlike projection.lag: this measures
        // "publish to consumer start" and §13.7's row says so, where the
        // projection metric measures when the read model became correct.
        metrics.Delivered(typeof(TEvent).Name, clock.GetUtcNow() - context.Message.OccurredAt);

        // Configuring this consumer for TEvent is a statement that something
        // handles TEvent, so zero handlers is a misconfiguration — and acking
        // is worse here than anywhere else: the inbox filter (§9.5) commits its
        // row once Consume returns, so redelivery is suppressed and the message
        // is gone for good. Throwing sends it to retry and then the error
        // queue, which §13.6 alerts on. One of the two sites in §9.4's "empty
        // is a decision" table that must fail rather than proceed.
        //
        // Materialised rather than asked twice: `handlers` is a lazily
        // resolved enumerable, so counting it and then iterating it would ask
        // the container for a second set of scoped instances.
        IIntegrationEventHandler<TEvent>[] resolved = [.. handlers];

        if (resolved.Length == 0)
        {
            throw new InvalidOperationException(
                $"No IIntegrationEventHandler<{typeof(TEvent).Name}> is registered, " +
                $"but {typeof(TEvent).Name} is bound on this endpoint. Check the §6.2 scan.");
        }

        // Duplicate suppression happens in the inbox filter (§9.5), configured
        // on the receive endpoint ahead of this consumer. Sequential for
        // ProjectionInvoker's reason: two handlers writing the same read table
        // in parallel is a deadlock waiting for load to find it.
        foreach (IIntegrationEventHandler<TEvent> handler in resolved)
            await handler.HandleAsync(context.Message, context.CancellationToken);
    }
}
