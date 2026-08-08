using Common.Application;

namespace Catalog.Application.Products.PublishProduct;

/// <summary>
/// Imperative, named for the business intent, immutable (§6.4). Bound directly
/// from the request body: a separate request record earns its place when the
/// wire shape and the command diverge — §11.4's enum parse, §8.5's idempotency
/// key — and here they are identical primitives.
/// </summary>
/// <remarks>
/// No <c>CommandId</c> and no <c>IIdempotentCommand</c>: §8.5's behaviour does
/// not exist yet, and §6.4 itself warns that the field without the interface
/// is unprotected. Both join with the PR that builds the behaviour.
/// </remarks>
public sealed record PublishProductCommand(
    string Name,
    string? ThumbnailUrl,
    decimal Amount,
    string Currency) : ICommand<Result<Guid>>;
