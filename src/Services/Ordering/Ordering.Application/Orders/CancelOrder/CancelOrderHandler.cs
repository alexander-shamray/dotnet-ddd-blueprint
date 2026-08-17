using Common.Application;
using Common.Domain;
using Ordering.Domain.Orders;

namespace Ordering.Application.Orders.CancelOrder;

/// <summary>
/// §11.4's resource-level check. Coarse permission checks live at the
/// endpoint; "is this the customer's own order?" belongs here, where the data
/// is available.
/// </summary>
/// <remarks>
/// <b>Public, where §11.4 prints <c>internal sealed</c>, and the difference is
/// not cosmetic.</b> §6.2's scan is <c>AddClasses(…)</c>, which registers
/// public classes only — an internal handler is skipped in silence, and the
/// dispatcher then throws on the first request that needs it rather than at
/// startup, because nothing resolves an open generic at build time. Observed
/// as a 500 on every cancellation before this line changed. Catalog's two
/// handlers are public for the same reason; the chapter is what moves.
/// </remarks>
public sealed class CancelOrderHandler(IOrderRepository orders, ICurrentUser currentUser, TimeProvider clock)
    : ICommandHandler<CancelOrderCommand, Result>
{
    public async Task<Result> HandleAsync(CancelOrderCommand command, CancellationToken ct)
    {
        Order? order = await orders.GetAsync(new OrderId(command.OrderId), ct);
        if (order is null)
            return Result.Failure(OrderErrors.NotFound);

        // Two propositions, and only one of them is about the caller. The
        // system path says so on the command; every other path needs an
        // authenticated owner, and gets a 404 rather than a 403, because a 403
        // confirms the order exists.
        //
        // "orders:admin" is a literal and not a constant from
        // OrderingPermissions, which §11.4 is explicit about: that class holds
        // the policies endpoints name, and this is a claim checked against a
        // loaded aggregate. Naming it from the same vocabulary would make it
        // read as a third policy, and a policy nobody registered resolves to
        // nothing. The class also lives in Ordering.Api, which this layer
        // cannot reference — the placement and the distinction agree.
        if (!command.IsSystemInitiated &&
            (!currentUser.IsAuthenticated ||
                (order.CustomerId.Value != currentUser.Id &&
                    !currentUser.HasPermission("orders:admin"))))
        {
            return Result.Failure(OrderErrors.NotFound);
        }

        // The aggregate still owns the transition — this handler decides who
        // may ask, not whether the order is in a state that permits it (§5.4).
        // A shipped order throws, and that is a rule rather than a bug: it is
        // translated here because the caller asked for something the model
        // refuses, which is a 422 and not a 500.
        try
        {
            order.Cancel(command.Reason, clock.GetUtcNow());
        }
        catch (DomainException)
        {
            return Result.Failure(OrderErrors.AlreadyShipped);
        }

        // No metric here, for the reason §6.4 gives: this runs inside the
        // transaction, and a cancellation counted before the commit is counted
        // again by an execution-strategy replay. It is recorded by the
        // projection, from OrderCancelledDomainEvent (§13.3).

        // No SaveChangesAsync: TransactionBehavior owns the commit (§6.3).
        return Result.Success();
    }
}
