namespace Catalog.Domain.Products;

/// <summary>
/// Collection-like access to the aggregate root, one per aggregate (§5.6).
/// Defined in Domain, implemented in Infrastructure.
/// </summary>
/// <remarks>
/// <c>Add</c> only, for now. §5.6's shape includes <c>GetAsync</c>, but
/// nothing in this service yet loads a product in order to change it, and an
/// unused member is the same untruth as an unused project reference —
/// <c>GetAsync</c> arrives with the first command that loads. There is no
/// <c>Update</c> (the unit of work tracks changes) and never a
/// <c>GetAll</c> or <c>IQueryable</c>: reads go through Dapper, not through
/// repositories (§6.5).
/// </remarks>
public interface IProductRepository
{
    void Add(Product product);
}
