using System.Net;
using System.Net.Http.Json;
using Shouldly;
using Web.Bff.Endpoints;
using Web.Bff.TestSupport;
using Xunit;

namespace Web.Bff.Tests;

/// <summary>
/// The consumer's half of PR-26: every expectation in
/// <see cref="PricingContract"/>, driven through the BFF's own screen against a
/// stub that answers exactly what the contract promises.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is what makes the contract consumer-DRIVEN rather than a second
/// provider suite.</b> <c>PricingContractVerificationTests</c> holds the real
/// Catalog to the same list; this one establishes that the list is a list of
/// things the consumer actually needs, because each entry is exercised by the
/// endpoint that needs it. An expectation nobody drives is an expectation the
/// provider is being held to for nothing, and §12.6 already argues that
/// direction one artefact over — a sample naming a contract that no longer
/// exists compiles until the type is deleted.
/// </para>
/// <para>
/// <b>The violation tests in <c>QuoteEndpointTests</c> are the other half and
/// are deliberately not here.</b> Those drive replies the contract forbids —
/// a comma decimal, a negative amount, a duplicate, a product nobody asked
/// about — and assert the endpoint refuses them. A contract says what the
/// provider owes; that suite says what the consumer does when it is not paid.
/// </para>
/// </remarks>
public sealed class PricingContractTests : IAsyncLifetime
{
    private readonly StubCatalog _catalog = new();

    private BffFactory _factory = null!;

    /// <summary>The interactions the contract says are answered.</summary>
    public static TheoryData<string> Answered => [.. PricingContract.Answered];

    /// <summary>The interactions the contract says are refused.</summary>
    public static TheoryData<string> Refused => [.. PricingContract.Refusals];

    public async ValueTask InitializeAsync()
    {
        await _catalog.InitializeAsync();

        _factory = new BffFactory { PricingAddress = _catalog.Address };
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _catalog.DisposeAsync();
    }

    [Theory]
    [MemberData(nameof(Answered))]
    public async Task The_stub_answers_what_the_contract_promises(string description)
    {
        PricingInteraction interaction = PricingContract.Named(description);
        IReadOnlyDictionary<string, Guid> published = _catalog.Publish(interaction);

        using HttpClient client = Caller();
        await client.GetAsync(Query(interaction, published), TestContext.Current.CancellationToken);

        // The same verification the provider run applies to the real Catalog's
        // reply. Both sides passing it is the whole guarantee this PR buys: the
        // consumer's suite is driven by a Catalog the real one could be, which
        // is precisely what a hand-written stub cannot promise.
        PricingContract.Verify(interaction, published, _catalog.Replies.ShouldHaveSingleItem());
    }

    [Theory]
    [MemberData(nameof(Answered))]
    public async Task The_quote_is_the_one_the_contract_implies(string description)
    {
        PricingInteraction interaction = PricingContract.Named(description);
        IReadOnlyDictionary<string, Guid> published = _catalog.Publish(interaction);
        PricingOutcome.Priced priced = (PricingOutcome.Priced)interaction.Then;

        using HttpClient client = Caller();
        QuoteResponse? quote = await client.GetFromJsonAsync<QuoteResponse>(
            Query(interaction, published),
            TestContext.Current.CancellationToken);

        Guid[] expected = [.. priced.Aliases.Select(alias => published[alias])];

        quote.ShouldNotBeNull();
        quote.Currency.ShouldBe(interaction.Currency);
        quote.Lines.Select(line => line.ProductId).ShouldBe(expected, ignoreOrder: true);
        quote.Total.ShouldBe(priced.Aliases.Sum(alias => PricingContract.Product(interaction, alias).Amount));

        // Everything asked about that the contract does not price, named rather
        // than dropped — which is the promise QuoteResponse.Unpriced makes and
        // the reason "absent, never zero" is an interaction at all.
        quote.Unpriced.ShouldBe(
            [.. PricingContract.RequestedIds(interaction, published).Where(id => !expected.Contains(id))],
            ignoreOrder: true);
    }

    [Theory]
    [MemberData(nameof(Refused))]
    public async Task A_refusal_the_contract_promises_reaches_the_caller_as_a_bad_request(string description)
    {
        PricingInteraction interaction = PricingContract.Named(description);
        IReadOnlyDictionary<string, Guid> published = _catalog.Publish(interaction);

        using HttpClient client = Caller();
        using HttpResponseMessage response = await client.GetAsync(
            Query(interaction, published),
            TestContext.Current.CancellationToken);

        // 400 rather than 500, through UpstreamExceptionHandler's
        // InvalidArgument arm. CheckoutEndpoints holds no ceiling of its own and
        // says so in a comment; this is the test that makes the comment true.
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    private HttpClient Caller()
    {
        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, "customer-1");

        return client;
    }

    /// <summary>
    /// The interaction as the BFF's own caller would ask it.
    /// </summary>
    /// <remarks>
    /// Through the query string rather than through
    /// <c>PricingContract.Request</c>, deliberately: the consumer's half has to
    /// establish that the request the ENDPOINT builds is the one the contract
    /// describes. Handing the endpoint's job to the contract would verify the
    /// contract against itself.
    /// </remarks>
    private static string Query(
        PricingInteraction interaction,
        IReadOnlyDictionary<string, Guid> published)
    {
        IEnumerable<string> ids = PricingContract
            .RequestedIds(interaction, published)
            .Select(id => $"productId={id}");

        return $"/v1/checkout/quote?{string.Join('&', ids)}&currency={interaction.Currency}";
    }
}
