using Microsoft.EntityFrameworkCore;
using Ordering.Domain.Orders;

namespace Ordering.Infrastructure.Persistence;

/// <summary>
/// §5.6's implementation half, matching the port: <c>GetAsync</c> and
/// <c>Add</c>, no <c>Update</c> (the unit of work tracks changes) and no query
/// surface — reads are Dapper's (§6.5).
/// </summary>
internal sealed class OrderRepository(OrderingDbContext db) : IOrderRepository
{
    /// <summary>
    /// The lines come with it. An aggregate is loaded to be changed, and every
    /// transition on <see cref="Order"/> reads <c>Total</c>, which sums them —
    /// so a lazy or absent collection would either throw or compute a total
    /// over nothing. Loading the whole aggregate in one query is §5.6's point
    /// rather than an optimisation.
    /// </summary>
    public Task<Order?> GetAsync(OrderId id, CancellationToken ct) =>
        db.Orders
            .Include(o => o.Lines)
            .SingleOrDefaultAsync(o => o.Id == id, ct);

    public void Add(Order order) => db.Add(order);
}
