using Catalog.Domain.Products;

namespace Catalog.Infrastructure.Persistence;

/// <summary>
/// §5.6's implementation half. <c>Add</c> only, matching the port: no
/// <c>Update</c> (the unit of work tracks changes), no query surface (reads
/// are Dapper's, §6.5), and <c>GetAsync</c> arrives with the first command
/// that loads an aggregate to change it.
/// </summary>
internal sealed class ProductRepository(CatalogDbContext db) : IProductRepository
{
    public void Add(Product product) => db.Add(product);
}
