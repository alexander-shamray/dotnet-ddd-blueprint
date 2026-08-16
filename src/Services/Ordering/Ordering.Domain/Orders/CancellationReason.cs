namespace Ordering.Domain.Orders;

/// <summary>
/// Why an order was cancelled. Carried on <c>OrderCancelledDomainEvent</c>,
/// and §13.3 tags a metric with it — which is what makes a closed set
/// load-bearing rather than tidy: a tag whose value set is unbounded is a
/// cardinality incident.
/// </summary>
/// <remarks>
/// §11.4's <c>CancellationReasons</c> maps the wire vocabulary onto these,
/// and deliberately refuses a code it does not know rather than defaulting —
/// a sibling service sending an unknown reason is a deployment problem, and
/// no amount of backoff resolves it. A member added here needs an entry
/// there in the same change.
/// </remarks>
public enum CancellationReason
{
    OutOfStock,
    StockTimeout,
    PaymentDeclined,
    PaymentTimeout,
    CustomerRequest
}
