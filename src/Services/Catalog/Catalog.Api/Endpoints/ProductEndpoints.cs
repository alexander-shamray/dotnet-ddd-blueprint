using Catalog.Application.Products.GetProducts;
using Catalog.Application.Products.PublishProduct;
using Common.Application;
using Common.Web;

namespace Catalog.Api.Endpoints;

/// <summary>
/// One static class per aggregate (ADR-015), in the namespace the §4.2 gate
/// selects on. The group is <c>/v1/catalog/products</c> because the gateway
/// strips <c>/api</c> from <c>/api/v1/catalog/{**catch-all}</c> (§10.2) — the
/// service sees the version, never the <c>/api</c> prefix, and PR-17's config
/// test will assert this group against the route's stripped path.
/// </summary>
/// <remarks>
/// No <c>RequireAuthorization</c>: the endpoints are deliberately
/// unauthenticated, stated in <c>deploy/compose/README.md</c>, and closed by
/// PR-16 (Appendix C).
/// </remarks>
public static class ProductEndpoints
{
    public static void MapProductEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app
            .MapGroup("/v1/catalog/products")
            .WithTags("Products");

        // The command binds straight from the body — a separate request
        // record earns its place when the wire shape and the command diverge
        // (§11.4's enum parse), and here they are identical primitives.
        group
            .MapPost(
                "/",
                async (PublishProductCommand command, IDispatcher dispatcher, CancellationToken ct) =>
                {
                    Result<Guid> result = await dispatcher.SendAsync(command, ct);

                    return result.ToHttpResult();
                })
            .WithName("PublishProduct");

        // The query's own result type — CursorPage, not Result (§6.2) — so
        // ToHttpResult has no part here.
        group
            .MapGet(
                "/",
                async (string? cursor, IDispatcher dispatcher, CancellationToken ct, int limit = 20) =>
                {
                    CursorPage<ProductSummaryDto> page =
                        await dispatcher.QueryAsync(new GetProductsQuery(cursor, limit), ct);

                    return Results.Ok(page);
                })
            .WithName("GetProducts");
    }
}
