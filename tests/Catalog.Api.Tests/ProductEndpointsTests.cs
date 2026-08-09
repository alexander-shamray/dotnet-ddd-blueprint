using System.Net;
using System.Net.Http.Json;
using Catalog.TestSupport;
using Shouldly;
using Xunit;

namespace Catalog.Api.Tests;

/// <summary>
/// §12.4's third level: HTTP in, HTTP out, covering what the levels below
/// structurally cannot — status codes and serialisation. The authorization
/// half of that mandate is PR-16's; these endpoints are deliberately
/// unauthenticated (Appendix C) and no test here pretends otherwise.
/// </summary>
[Collection(nameof(IntegrationCollection))]
public sealed class ProductEndpointsTests(ServiceFixture fixture) : IAsyncLifetime
{
    private HttpClient _client = null!;

    public async ValueTask InitializeAsync()
    {
        _client = fixture.Factory.CreateClient();
        await fixture.ResetAsync();
    }

    public ValueTask DisposeAsync()
    {
        _client.Dispose();
        return ValueTask.CompletedTask;
    }

    private sealed record PageDto(List<ItemDto> Items, string? NextCursor);

    private sealed record ItemDto(
        Guid ProductId,
        string Name,
        string? ThumbnailUrl,
        decimal Amount,
        string Currency,
        DateTimeOffset PublishedAt);

    private Task<HttpResponseMessage> PublishAsync(string name, decimal amount = 10m) =>
        _client.PostAsJsonAsync(
            "/v1/catalog/products",
            new { Name = name, ThumbnailUrl = (string?)null, Amount = amount, Currency = "EUR" },
            TestContext.Current.CancellationToken);

    [Fact]
    public async Task Publishing_a_product_returns_200_with_the_new_id()
    {
        // 200 with a Guid body is also the overload pin §10.5 warns about: a
        // Result<T> that resolved to the void ToHttpResult would 204 the id
        // away, and only this boundary can see that.
        HttpResponseMessage response = await PublishAsync("Walnut desk");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        Guid id = await response.Content.ReadFromJsonAsync<Guid>(TestContext.Current.CancellationToken);
        id.ShouldNotBe(Guid.Empty);
    }

    [Fact]
    public async Task Publishing_an_invalid_product_returns_400_with_field_keyed_errors()
    {
        // The ValidationBehavior path over the wire (§6.3 → §10.5): thrown
        // ValidationException, translated to problem+json with an errors
        // dictionary keyed by field — no handler ran, no row exists.
        HttpResponseMessage response = await PublishAsync("", amount: -1m);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.ShouldContain("Name");
        body.ShouldContain("Amount");
    }

    [Fact]
    public async Task The_published_product_comes_back_through_the_listing()
    {
        // The whole slice, wire to wire: POST persists through the real
        // pipeline and transaction behaviour, GET reads it back over Dapper.
        HttpResponseMessage published = await PublishAsync("Walnut desk", 19.99m);
        Guid id = await published.Content.ReadFromJsonAsync<Guid>(TestContext.Current.CancellationToken);

        PageDto? page = await _client.GetFromJsonAsync<PageDto>(
            "/v1/catalog/products",
            TestContext.Current.CancellationToken);

        page.ShouldNotBeNull();
        ItemDto item = page.Items.ShouldHaveSingleItem();
        item.ProductId.ShouldBe(id);
        item.Name.ShouldBe("Walnut desk");
        item.Amount.ShouldBe(19.99m);
        item.Currency.ShouldBe("EUR");
        page.NextCursor.ShouldBeNull("one row is one page");
    }

    [Fact]
    public async Task The_listing_pages_forward_with_the_returned_cursor()
    {
        // Three in-process POSTs can share one clock tick, and within a tied
        // PublishedAt the id tiebreak is not publish order — so this asserts
        // the paging mechanics (no overlap, nothing skipped, a terminal null),
        // and the deterministic ordering lives in the handler tests, where
        // the seeding controls the clock.
        List<Guid> published = [];
        string[] names = ["First", "Second", "Third"];
        foreach (string name in names)
        {
            HttpResponseMessage response = await PublishAsync(name);
            response.EnsureSuccessStatusCode();
            published.Add(await response.Content.ReadFromJsonAsync<Guid>(TestContext.Current.CancellationToken));
        }

        PageDto? first = await _client.GetFromJsonAsync<PageDto>(
            "/v1/catalog/products?limit=2",
            TestContext.Current.CancellationToken);

        first.ShouldNotBeNull();
        first.Items.Count.ShouldBe(2);
        first.NextCursor.ShouldNotBeNull();

        PageDto? second = await _client.GetFromJsonAsync<PageDto>(
            $"/v1/catalog/products?limit=2&cursor={Uri.EscapeDataString(first.NextCursor)}",
            TestContext.Current.CancellationToken);

        second.ShouldNotBeNull();
        second.Items.ShouldHaveSingleItem();
        second.NextCursor.ShouldBeNull();

        // No overlap, nothing skipped (ADR-016's whole point).
        Guid[] seen = [.. first.Items.Concat(second.Items).Select(i => i.ProductId)];
        seen.ShouldBeUnique();
        seen.ShouldBe(published, ignoreOrder: true);
    }
}
