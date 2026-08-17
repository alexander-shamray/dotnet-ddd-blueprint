using Common.Application;
using Ordering.Domain.Orders;

namespace Ordering.Application.Orders;

/// <summary>
/// The catalogue. Every <see cref="Error"/> the service can return is
/// constructed here and nowhere else — which is what makes <c>Code</c> a
/// bounded set rather than whatever string the nearest handler happened to
/// type.
/// </summary>
/// <remarks>
/// <c>Code</c> is a metric dimension (§9.8 tags <c>command.domain_rejected</c>
/// with it), so it has to be closed: a tag whose value set is unbounded is a
/// cardinality incident waiting for the first handler that interpolates an id
/// into a code. Note what is not in any code below — no order id, no customer
/// id, no count. Those belong in the description, which is written for a
/// person and never tagged onto an instrument.
/// </remarks>
public static class OrderErrors
{
    public static readonly Error NotFound =
        Error.NotFound("order.not_found", "No order with that id.");

    public static readonly Error AlreadyShipped =
        Error.Rule("order.already_shipped", "A shipped order cannot be cancelled.");

    // 422, not 400: the request was well-formed and the validator passed it.
    // The products are unpriceable, which is a fact about this service's state
    // and not something the caller phrased wrongly.
    public static Error ProductsUnavailable(IReadOnlyList<ProductId> missing) =>
        Error.Rule("order.products_unavailable", $"No price for {missing.Count} product(s).");
}
