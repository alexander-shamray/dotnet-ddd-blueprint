using Catalog.Domain.Common;
using Catalog.Domain.Products;
using Common.Application;

namespace Catalog.Application.Products.PublishProduct;

/// <summary>
/// Thin by §6.4's design: build what the domain needs, call one domain
/// operation, return. No <c>SaveChanges</c> — the transaction behaviour owns
/// the commit — and no metric: "products published" is a count of commits,
/// and this line can still roll back or be replayed whole by the retrying
/// execution strategy (§6.3).
/// </summary>
public sealed class PublishProductHandler(IProductRepository products, TimeProvider clock)
    : ICommandHandler<PublishProductCommand, Result<Guid>>
{
    public Task<Result<Guid>> HandleAsync(PublishProductCommand command, CancellationToken ct)
    {
        var product = Product.Publish(
            command.Name,
            command.ThumbnailUrl,
            Money.Of(command.Amount, command.Currency),
            clock.GetUtcNow());

        products.Add(product);

        return Task.FromResult(Result.Success(product.Id.Value));
    }
}
