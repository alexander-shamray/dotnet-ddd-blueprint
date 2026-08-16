using Common.Application;
using Common.Web;
using Ordering.Application.Orders;
using Ordering.Application.Orders.CancelOrder;
using Ordering.Application.Orders.PlaceOrder;
using Ordering.Domain.Orders;

namespace Ordering.Api.Endpoints;

/// <summary>
/// One static class per aggregate (ADR-015), in the namespace the §4.2 gate
/// selects on. The group is <c>/v1/orders</c> because the gateway strips
/// <c>/api</c> from <c>/api/v1/orders/{**catch-all}</c> (§10.2) — the service
/// sees the version, never the <c>/api</c> prefix.
/// </summary>
/// <remarks>
/// Nothing here is anonymous, and unlike Catalog there is no asymmetry to
/// argue: §10.2's <c>ordering</c> route carries <c>AuthorizationPolicy:
/// authenticated</c>, so the edge admits no unauthenticated caller to any of
/// it. An order belongs to somebody, which is the whole difference from a
/// product catalogue.
/// </remarks>
public static class OrderEndpoints
{
    public static void MapOrderEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app
            .MapGroup("/v1/orders")
            .WithTags("Orders")
            // Fail closed at the group (§11.4's shape): an endpoint added here
            // later inherits authentication rather than arriving open, so
            // forgetting a line makes a new endpoint unreachable instead of
            // public.
            .RequireAuthorization();

        // The command binds straight from the body. It carries no CustomerId
        // to bind — §11.4's subject rule — so the wire shape and the command
        // are identical and a separate request record would earn nothing.
        group
            .MapPost(
                "/",
                async (PlaceOrderCommand command, IDispatcher dispatcher, CancellationToken ct) =>
                {
                    Result<Guid> result = await dispatcher.SendAsync(command, ct);

                    return result.ToHttpResult();
                })
            .RequireAuthorization(OrderingPermissions.Write)
            .WithName("PlaceOrder");

        // A request record rather than the command, because the wire shape and
        // the command genuinely diverge here — §11.4's enum parse. The route
        // carries the id, the body carries a reason code from §11.4's
        // vocabulary, and the origin is not the caller's to state: a request
        // that could set InitiatedBy to System would be a request that skips
        // the ownership check.
        group
            .MapPost(
                "/{id:guid}/cancellation",
                async (
                    Guid id,
                    CancelOrderRequest request,
                    IDispatcher dispatcher,
                    CancellationToken ct) =>
                {
                    if (!CancellationReasons.TryParse(request.Reason, out CancellationReason reason))
                        return Results.ValidationProblem(
                            new Dictionary<string, string[]>
                            {
                                [nameof(request.Reason)] = ["Not a known cancellation reason."]
                            });

                    Result result = await dispatcher.SendAsync(
                        new CancelOrderCommand(id, reason, CommandOrigin.Customer),
                        ct);

                    return result.ToHttpResult();
                })
            .RequireAuthorization(OrderingPermissions.Cancel)
            .WithName("CancelOrder");
    }
}

/// <summary>
/// The body of a cancellation. One member, and it is a string rather than the
/// enum: an unknown reason must be a 400 naming the field, not a model-binding
/// failure whose message names the enum type.
/// </summary>
public sealed record CancelOrderRequest(string Reason);
