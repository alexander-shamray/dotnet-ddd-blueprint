using Common.Application;
using Common.Domain;
using Ordering.Domain.Orders;

namespace Ordering.Application.Orders.MarkOrderShipped;

/// <summary>
/// The transition §9.6's saga asks for on <c>ShipmentDispatched</c>, and the
/// last one the saga makes before finalising.
/// </summary>
public sealed class MarkOrderShippedHandler(IOrderRepository orders, TimeProvider clock)
    : ICommandHandler<MarkOrderShippedCommand, Result>
{
    public async Task<Result> HandleAsync(MarkOrderShippedCommand command, CancellationToken ct)
    {
        Order? order = await orders.GetAsync(new OrderId(command.OrderId), ct);
        if (order is null)
            return Result.Failure(OrderErrors.NotFound);

        // The same split ConfirmOrderHandler makes, one state later. An order
        // that has not reached Confirmed yet is waiting on a command still in
        // flight — Shipping cannot despatch what was never confirmed, so the
        // fact this arrived is evidence the confirmation exists and has not
        // landed. Time fixes that; §9.8's backoff is what waits.
        if (order.Status is OrderStatus.AwaitingStock or OrderStatus.AwaitingPayment)
            return Result.Failure(OrderErrors.NotConfirmed);

        try
        {
            order.MarkShipped(command.Tracking, clock.GetUtcNow());
        }
        catch (DomainException)
        {
            // Cancelled, Shipped or Delivered: no retry changes any of them.
            return Result.Failure(OrderErrors.NotShippable);
        }

        return Result.Success();
    }
}
