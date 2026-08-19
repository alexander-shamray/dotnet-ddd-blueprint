using Common.Application;
using Common.Domain;
using Ordering.Domain.Orders;

namespace Ordering.Application.Orders.ConfirmOrder;

/// <summary>
/// The aggregate transition §9.6's saga asks for once a payment is authorised.
/// The handler decides nothing about whether the order may be confirmed —
/// <see cref="Order.ConfirmPayment"/> owns that (§5.4); what this decides is
/// which refusals are answers and which are worth retrying.
/// </summary>
/// <remarks>
/// <b>Public for §6.2's scan</b>, on <c>CancelOrderHandler</c>'s terms:
/// <c>AddClasses(…)</c> registers public classes only, so an internal handler
/// is skipped in silence and the dispatcher throws on the first message that
/// needs it.
/// <para>
/// <b>No ownership check, and its absence is the same decision the missing
/// <c>CommandOrigin</c> is.</b> §11.4's check exists to stop one customer
/// acting on another's order; this command has no customer behind it and **no
/// HTTP route to arrive through** — it arrives on <c>ordering-commands</c> and
/// nowhere else — so there is no principal to compare against and nothing a
/// caller could assert. What guards it is that arrival, with the weakness
/// §9.4's callout states in full.
/// </para>
/// </remarks>
public sealed class ConfirmOrderHandler(IOrderRepository orders, TimeProvider clock)
    : ICommandHandler<ConfirmOrderCommand, Result>
{
    public async Task<Result> HandleAsync(ConfirmOrderCommand command, CancellationToken ct)
    {
        Order? order = await orders.GetAsync(new OrderId(command.OrderId), ct);
        if (order is null)
            return Result.Failure(OrderErrors.NotFound);

        // The one refusal here that time fixes, and it has to be told apart
        // from the rest before the aggregate is asked. Ordering learns that
        // stock was reserved on ordering-stock-events and that payment was
        // authorised on ordering-commands — two receive endpoints, two
        // deliveries, no ordering between them — so a confirmation can arrive
        // while the order is still AwaitingStock. §9.8's rule decides the
        // answer: Unavailable is a fault that time might fix, CommandConsumer
        // turns it into an UnavailableResultException, and the endpoint's
        // backoff runs it again. Returning a Rule error here would ack it,
        // count it as a domain rejection, and leave a paid order unconfirmed
        // for good.
        //
        // The window is a local write against a payment authorisation, so it
        // is small; that it is small is not why it is handled.
        if (order.Status is OrderStatus.AwaitingStock)
            return Result.Failure(OrderErrors.StockNotConfirmed);

        try
        {
            order.ConfirmPayment(command.Reference, clock.GetUtcNow());
        }
        catch (DomainException)
        {
            // Every other state is terminal for this command: an order already
            // Confirmed, Shipped or Cancelled will refuse identically on the
            // fifth attempt, so this is an answer rather than a fault (§9.8).
            return Result.Failure(OrderErrors.NotAwaitingPayment);
        }

        // No SaveChangesAsync: TransactionBehavior owns the commit (§6.3), and
        // it is also what stages OrderConfirmedDomainEvent onto the outbox —
        // which is the reason this arrives as a command rather than as work
        // done inside an integration-event handler (§7.5).
        return Result.Success();
    }
}
