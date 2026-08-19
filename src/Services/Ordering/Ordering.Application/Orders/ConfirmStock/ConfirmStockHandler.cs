using Common.Application;
using Common.Domain;
using Ordering.Domain.Orders;

namespace Ordering.Application.Orders.ConfirmStock;

/// <summary>
/// Moves the order from <c>AwaitingStock</c> to <c>AwaitingPayment</c> (§5.4).
/// </summary>
public sealed class ConfirmStockHandler(IOrderRepository orders, TimeProvider clock)
    : ICommandHandler<ConfirmStockCommand, Result>
{
    public async Task<Result> HandleAsync(ConfirmStockCommand command, CancellationToken ct)
    {
        Order? order = await orders.GetAsync(new OrderId(command.OrderId), ct);
        if (order is null)
            return Result.Failure(OrderErrors.NotFound);

        try
        {
            order.ConfirmStock(clock.GetUtcNow());
        }
        catch (DomainException)
        {
            // Not Unavailable, and this is the one handler in the saga's four
            // where nothing earlier can still be in flight: AwaitingStock is
            // the state Order.Place leaves behind, so anything else means the
            // order has already moved on. The interesting case is Cancelled —
            // a stock timeout fired, the saga cancelled and finalised, and
            // Inventory's reservation arrived afterwards. That is a rejection
            // worth counting rather than retrying: the reservation is stranded
            // in Inventory and no attempt from here releases it.
            return Result.Failure(OrderErrors.NotAwaitingStock);
        }

        return Result.Success();
    }
}
