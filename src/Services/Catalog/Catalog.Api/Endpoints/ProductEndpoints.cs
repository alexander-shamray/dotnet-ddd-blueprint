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
/// PR-16 closed the deliberately unauthenticated gap PR-10 shipped and
/// <c>deploy/compose/README.md</c> named. It did not close it uniformly, and
/// the asymmetry below is the point rather than an omission.
/// </remarks>
public static class ProductEndpoints
{
    public static void MapProductEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app
            .MapGroup("/v1/catalog/products")
            .WithTags("Products")
            // Fail closed at the group (§11.4's shape): an endpoint added here
            // later inherits authentication rather than arriving open, so
            // forgetting a line makes a new endpoint unreachable instead of
            // public. The one exception states itself, one method down.
            .RequireAuthorization();

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
            .RequireAuthorization(CatalogPermissions.Write)
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
            // Anonymous, deliberately and permanently — this is not the gap
            // PR-16 closed but the shape §10.2 already specifies. The gateway's
            // `catalog-public` route matches GET alone, names `anonymous` as
            // its AuthorizationPolicy — YARP's reserved value — and rate-limits
            // under a policy of the same name; a product listing is public, and
            // requiring a token here would make the route unusable at the edge
            // that publishes it.
            //
            // Stated rather than inherited by omission. The group above fails
            // closed, so an anonymous endpoint has to say so out loud, and the
            // reader can tell a decision from a forgotten line.
            .AllowAnonymous()
            .WithName("GetProducts");
    }
}
