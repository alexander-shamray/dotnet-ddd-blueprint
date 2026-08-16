using Common.Application;
using Ordering.Domain.Common;
using Ordering.Domain.Orders;

namespace Ordering.Application.Orders.PlaceOrder;

/// <summary>
/// §6.4's command handler. Thin by design: it loads what the domain needs,
/// calls one domain operation, and returns. Every business rule — line
/// merging, currency consistency, minimum one line — lives in
/// <see cref="Order"/>.
/// </summary>
/// <remarks>
/// <see cref="ICurrentUser"/> (§11.4) is the only source of the subject on
/// this path. A command that reaches here is HTTP-borne — nothing publishes
/// <c>PlaceOrder</c> as a message — so the principal is always present, and
/// <c>Id</c> throwing on an unauthenticated call is the right failure rather
/// than a case to guard. The endpoint's <c>RequireAuthorization</c> is what
/// makes that true, and it fails closed if it is ever removed.
/// </remarks>
public sealed class PlaceOrderHandler(
    IOrderRepository orders,
    IProductPriceReader prices,
    ICurrentUser currentUser,
    TimeProvider clock)
    : ICommandHandler<PlaceOrderCommand, Result<Guid>>
{
    public async Task<Result<Guid>> HandleAsync(PlaceOrderCommand command, CancellationToken ct)
    {
        ProductId[] productIds = [.. command.Items.Select(i => new ProductId(i.ProductId))];
        IReadOnlyDictionary<ProductId, Money> priceList =
            await prices.GetAsync(productIds, command.Currency, ct);

        ProductId[] missing = [.. productIds.Where(id => !priceList.ContainsKey(id))];
        if (missing.Length > 0)
            return Result.Failure<Guid>(OrderErrors.ProductsUnavailable(missing));

        IEnumerable<(ProductId Product, int Quantity, Money UnitPrice)> items =
            command.Items.Select(i =>
            {
                var id = new ProductId(i.ProductId);
                return (id, i.Quantity, priceList[id]);
            });

        var order = Order.Place(
            new CustomerId(currentUser.Id),
            command.ShippingAddress.ToDomain(),
            items,
            command.Currency,
            clock.GetUtcNow());

        orders.Add(order);

        // No metric here. "Orders placed" is a count of orders that committed,
        // and this line runs inside a transaction that may still roll back —
        // or be replayed whole by EF's retrying execution strategy (§6.3),
        // which would count the same order once per attempt. It is recorded by
        // the projection instead (§13.3).
        return Result.Success(order.Id.Value);
    }
}
