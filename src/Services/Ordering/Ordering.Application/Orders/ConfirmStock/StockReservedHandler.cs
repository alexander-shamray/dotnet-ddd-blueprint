using Common.Application;
using Common.Contracts.Inventory.V1;

namespace Ordering.Application.Orders.ConfirmStock;

/// <summary>
/// Ordering's own reaction to <c>StockReserved</c> (§3.2's Consumes column),
/// as distinct from the saga's. The saga reads the same fact to decide what to
/// ask for next; this records it on the order.
/// </summary>
/// <remarks>
/// <b>Two consumers of one event is not a duplication.</b> §9.6 is explicit
/// that the saga holds only coordination state and never business logic, so it
/// cannot be the thing that moves the aggregate — and §3.2 gives Ordering no
/// command for stock confirmation to arrive by. The event has two readers
/// because it means two things to this service.
/// <para>
/// It dispatches rather than mutating, for the reason
/// <see cref="ConfirmStockCommand"/> states: the command pipeline is what
/// opens the transaction and stages the domain event.
/// </para>
/// </remarks>
public sealed class StockReservedHandler(IDispatcher dispatcher)
    : IIntegrationEventHandler<StockReserved>
{
    public async Task HandleAsync(StockReserved integrationEvent, CancellationToken ct)
    {
        // The Result is deliberately dropped, and dropping it is the same
        // decision CommandConsumer makes explicitly (§9.8): every failure this
        // command can return is an answer rather than a fault. Throwing on one
        // would retry a rejection five times and then put a stock reservation
        // in the error queue, where §13.6's depth alert pages a human for an
        // order that was cancelled hours ago.
        //
        // What keeps that from being silent is the same thing that keeps
        // CommandConsumer's ack from being silent — LoggingBehavior records
        // the outcome of every dispatched command (§13.3) — with one honest
        // difference: this path does not increment command.domain_rejected,
        // because that counter is CommandConsumer's and this command never
        // touches the wire.
        await dispatcher.SendAsync(new ConfirmStockCommand(integrationEvent.OrderId), ct);
    }
}
