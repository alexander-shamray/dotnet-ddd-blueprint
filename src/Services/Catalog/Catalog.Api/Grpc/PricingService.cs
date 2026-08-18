using System.Globalization;
using Catalog.Application.Products.GetPrices;
using Catalog.Pricing.V1;
using Common.Application;
using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using PricingGrpc = Catalog.Pricing.V1.Pricing;

namespace Catalog.Api.Grpc;

/// <summary>
/// The server half of §9.7's one permitted synchronous hop. It is a transport
/// adapter and nothing else: parse, dispatch, project onto the reply — the same
/// job <c>ProductEndpoints</c> does for HTTP, which is why it holds to
/// Application contracts under the same §4.2 gate.
/// </summary>
/// <remarks>
/// <b>Authenticated, and that is what makes the credentials in §11.5 real.</b>
/// The data here is the same data <c>GET /v1/catalog/products</c> publishes
/// anonymously, so a reader could reasonably ask why this call needs a token at
/// all. Because the alternative is a platform whose only client-credentials
/// flow is decorative: the BFF holds a client secret, mints a token and
/// attaches it on every attempt, and if nothing ever checked it, the audience
/// mapper §11.5 spends a page on could be missing for a year with every test
/// green. A permission is deliberately *not* required on top — the BFF's
/// service account carries no <c>permission</c> claim (§11.4), and inventing
/// one for a public product listing would be a role in permission's clothing.
/// </remarks>
/// <remarks>
/// <b>The alias is not a preference, and §9.7's <c>Pricing.PricingClient</c>
/// still reads correctly on the other side.</b> The proto's
/// <c>csharp_namespace</c> is <c>Catalog.Pricing.V1</c>, so inside
/// <c>Catalog.Api.Grpc</c> the bare name <c>Pricing</c> is resolved by walking
/// the enclosing namespaces — and <c>Catalog</c> has a <c>Pricing</c> member,
/// the generated namespace itself. It binds there and never reaches the
/// <c>using</c>, so <c>Pricing.PricingBase</c> fails to compile with an error
/// naming a namespace the reader did not know existed. Web.Bff has no
/// enclosing <c>Catalog</c>, which is why the client half needs none of this.
/// </remarks>
[Authorize]
internal sealed class PricingService(IDispatcher dispatcher) : PricingGrpc.PricingBase
{
    public override async Task<GetPricesReply> GetPrices(GetPricesRequest request, ServerCallContext context)
    {
        // Parsed here rather than in the query, because it is a wire concern:
        // the contract says "GUIDs in their canonical text form" and a string
        // that is not one never becomes a ProductId. InvalidArgument rather
        // than the ValidationException path below, for the same reason
        // §11.4's enum parse is a request record's job and not a command's.
        Guid[] productIds = new Guid[request.ProductId.Count];

        for (int i = 0; i < request.ProductId.Count; i++)
        {
            // TryParseExact with "D", not TryParse: the contract says "GUIDs in
            // their canonical text form", and TryParse also accepts the N, B
            // and P formats — so "{11111111-...}" and the 32-digit run would
            // become valid product ids in a service whose own proto says they
            // are not. Accepting more than the contract states is how two ends
            // stop agreeing about what the contract is.
            if (!Guid.TryParseExact(request.ProductId[i], "D", out productIds[i]))
            {
                // The index, never the value. It is a caller-supplied string
                // arriving in an error message that reaches the logs, and
                // §13.4's redactor cannot see a value interpolated into a
                // message — the same rule the gateway's CORS guard follows.
                throw new RpcException(new Status(
                    StatusCode.InvalidArgument,
                    $"product_id[{i}] is not a GUID."));
            }
        }

        IReadOnlyList<ProductPriceDto> prices = await dispatcher.QueryAsync(
            new GetPricesQuery(productIds, request.Currency),
            context.CancellationToken);

        GetPricesReply reply = new();

        foreach (ProductPriceDto price in prices)
        {
            reply.Price.Add(new ProductPrice
            {
                ProductId = price.ProductId.ToString(),
                Name = price.Name,
                // InvariantCulture explicitly, on both sides of the wire. The
                // default would format 12.50 as "12,50" under a German locale
                // and the BFF's parse would then read it as 1250 — a
                // hundredfold price error that appears only on a host whose
                // culture differs from the developer's.
                Amount = price.Amount.ToString(CultureInfo.InvariantCulture),
                Currency = price.Currency
            });
        }

        return reply;
    }
}
