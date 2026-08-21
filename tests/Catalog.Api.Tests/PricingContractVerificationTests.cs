using System.Net.Http.Json;
using Catalog.Pricing.V1;
using Catalog.TestSupport;
using Grpc.Core;
using Grpc.Net.Client;
using Shouldly;
using Web.Bff.TestSupport;
using Xunit;
using PricingGrpc = Catalog.Pricing.V1.Pricing;

namespace Catalog.Api.Tests;

/// <summary>
/// The provider's half of PR-26: every expectation <c>Web.Bff</c> wrote down in
/// <see cref="PricingContract"/>, verified against the real Catalog.
/// </summary>
/// <remarks>
/// <para>
/// <b>The expectations are the consumer's and this file does not restate
/// one.</b> <c>PricingContract</c> is linked in from <c>tests/Web.Bff.TestSupport</c>
/// rather than referenced, exactly as <c>pricing.proto</c> is linked into
/// <c>Web.Bff</c> — so no assembly crosses a service boundary and §4.3 keeps
/// <c>Common.Contracts</c> as its one exception. Everything below is the
/// plumbing that realises a state, asks the question and hands the answer back
/// to the consumer's own verification.
/// </para>
/// <para>
/// <b>Not in <c>Platform.IntegrationTests</c>, which §4.1 calls the only suite
/// that references every service, and the reason is what a provider
/// verification needs.</b> It needs the provider running: a migrated SQL Server,
/// a broker for the bus to connect to and the real host over them. That is
/// <c>ServiceFixture</c>, and it is already here — homing this in the platform
/// suite would give a sixth project a container set (§12.4's stated price) to
/// run six tests that Catalog's own suite can run for nothing. §12.6's contract
/// tests stay where they are; those are about the shape of the contract
/// assembly, and this is about one service honouring one consumer.
/// </para>
/// <para>
/// <b>What Catalog does beyond the contract is still <c>PricingServiceTests</c>'
/// job.</b> The malformed id, the non-canonical GUID and the anonymous caller
/// are provider-owned behaviour that no consumer relies on, and moving them here
/// would turn a consumer-driven contract into an inventory of everything the
/// provider happens to do.
/// </para>
/// </remarks>
[Collection(nameof(IntegrationCollection))]
public sealed class PricingContractVerificationTests(ServiceFixture fixture) : IAsyncLifetime
{
    private HttpClient _client = null!;
    private GrpcChannel _channel = null!;

    /// <summary>The interactions the contract says are answered.</summary>
    public static TheoryData<string> Answered => [.. PricingContract.Answered];

    /// <summary>The interactions the contract says are refused.</summary>
    public static TheoryData<string> Refused => [.. PricingContract.Refusals];

    public async ValueTask InitializeAsync()
    {
        _client = fixture.Factory.CreateClient();
        _channel = GrpcChannel.ForAddress(
            fixture.Factory.Server.BaseAddress,
            new GrpcChannelOptions { HttpHandler = fixture.Factory.Server.CreateHandler() });

        await fixture.ResetAsync();
    }

    public ValueTask DisposeAsync()
    {
        _channel.Dispose();
        _client.Dispose();

        return ValueTask.CompletedTask;
    }

    [Theory]
    [MemberData(nameof(Answered))]
    public async Task Catalog_answers_what_the_contract_promises(string description)
    {
        PricingInteraction interaction = PricingContract.Named(description);
        IReadOnlyDictionary<string, Guid> published = await PublishAsync(interaction);

        GetPricesReply reply = await Pricing.GetPricesAsync(
            PricingContract.Request(interaction, published),
            Authenticated(),
            cancellationToken: TestContext.Current.CancellationToken);

        // The consumer's own tolerance, applied to the provider's own reply.
        // Nothing in this file decides what counts as an answer the BFF can use
        // — which is the whole difference between this and a second provider
        // suite.
        PricingContract.Verify(interaction, published, reply);
    }

    [Theory]
    [MemberData(nameof(Refused))]
    public async Task Catalog_refuses_what_the_contract_says_it_refuses(string description)
    {
        PricingInteraction interaction = PricingContract.Named(description);
        IReadOnlyDictionary<string, Guid> published = await PublishAsync(interaction);
        PricingOutcome.Refusal refusal = (PricingOutcome.Refusal)interaction.Then;

        RpcException thrown = await Should.ThrowAsync<RpcException>(
            () => Pricing
                .GetPricesAsync(
                    PricingContract.Request(interaction, published),
                    Authenticated(),
                    cancellationToken: TestContext.Current.CancellationToken)
                .ResponseAsync);

        // The status and not merely a failure: UpstreamExceptionHandler maps
        // InvalidArgument to the caller's 400 and everything it has not thought
        // about to a 500, so a refusal that arrived as any other status would
        // reach the customer as this platform's own fault.
        thrown.StatusCode.ShouldBe(refusal.Status);
    }

    /// <summary>
    /// The principal a validated client-credentials token becomes (§11.3), as
    /// call metadata — the BFF's service account, since it is the BFF's contract
    /// being verified.
    /// </summary>
    private static Metadata Authenticated() =>
        [new Metadata.Entry(TestAuthHandler.UserHeader, "service-account-web-bff")];

    private PricingGrpc.PricingClient Pricing => new(_channel);

    /// <summary>
    /// Realises an interaction's <c>Given</c> state against the real Catalog,
    /// and answers the id each product was published under.
    /// </summary>
    /// <remarks>
    /// Through <c>POST /v1/catalog/products</c> rather than through the
    /// repository, so the state the contract is verified against is one a
    /// customer could have produced — including <c>Money.Of</c>'s
    /// upper-casing, which is what makes the differently-spelled currency
    /// interaction test anything at all.
    /// </remarks>
    private async Task<IReadOnlyDictionary<string, Guid>> PublishAsync(PricingInteraction interaction)
    {
        Dictionary<string, Guid> published = [];

        foreach (ContractProduct product in interaction.Given)
        {
            HttpRequestMessage request = new(HttpMethod.Post, "/v1/catalog/products")
            {
                Content = JsonContent.Create(new
                {
                    product.Name,
                    ThumbnailUrl = (string?)null,
                    product.Amount,
                    product.Currency
                })
            };

            request.Headers.Add(TestAuthHandler.UserHeader, Guid.CreateVersion7().ToString());
            request.Headers.Add(TestAuthHandler.PermissionsHeader, CatalogPermissions.Write);

            HttpResponseMessage response = await _client.SendAsync(
                request,
                TestContext.Current.CancellationToken);

            response.EnsureSuccessStatusCode();

            published.Add(
                product.Alias,
                (await response.Content.ReadFromJsonAsync<Guid>(TestContext.Current.CancellationToken))!);
        }

        return published;
    }
}
