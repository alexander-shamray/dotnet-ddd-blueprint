namespace Ordering.Domain.Orders;

/// <summary>
/// Collection-like access to the aggregate root, one per aggregate (§5.6).
/// Defined in Domain, implemented in Infrastructure.
/// </summary>
/// <remarks>
/// <c>GetAsync</c> and <c>Add</c>, which is what this service's two slices
/// use. §5.6's shape also lists <c>GetByPaymentReferenceAsync</c>; nothing
/// loads an order by payment reference until §9.6's saga reconciles one, and
/// an unused member is the same untruth as an unused project reference — the
/// argument <c>IProductRepository</c> makes one service over, arriving at a
/// different subset because a different slice landed first. There is no
/// <c>Update</c> (the unit of work tracks changes) and never a <c>GetAll</c>
/// or <c>IQueryable</c>: reads go through Dapper, not through repositories
/// (§6.5).
/// </remarks>
public interface IOrderRepository
{
    Task<Order?> GetAsync(OrderId id, CancellationToken ct);

    void Add(Order order);
}
