using System.Net;
using System.Net.Http.Json;
using Catalog.Api;
using Catalog.TestSupport;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Catalog.Api.Tests;

/// <summary>
/// §12.4's third level: HTTP in, HTTP out, covering what the levels below
/// structurally cannot — status codes, serialisation and, since PR-16, the
/// endpoint's authorization.
/// </summary>
/// <remarks>
/// Every write here states a principal, and states the narrowest one that
/// works: §12.4's rule is that a fixture handing out a blanket claim set makes
/// the §11.4 policies untestable and, worse, makes them look tested — the
/// endpoints are reached, the assertions pass, and the one behaviour nobody
/// exercises is the refusal. So the two tests that assert a refusal grant
/// nothing and grant the wrong thing respectively.
/// </remarks>
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
        PostAsync(
            new { Name = name, ThumbnailUrl = (string?)null, Amount = amount, Currency = "EUR" },
            CatalogPermissions.Write);

    /// <summary>
    /// A publish request as a caller holding <paramref name="permissions"/> —
    /// a space-separated grant, or null for no principal at all. Explicit at
    /// every call site rather than defaulted into the client's headers: a
    /// default grant is how a suite ends up proving the policy is applied by
    /// never once arriving without it.
    /// </summary>
    private Task<HttpResponseMessage> PostAsync(object body, string? permissions)
    {
        HttpRequestMessage request = new(HttpMethod.Post, "/v1/catalog/products")
        {
            Content = JsonContent.Create(body)
        };

        if (permissions is not null)
        {
            request.Headers.Add(TestAuthHandler.UserHeader, Guid.CreateVersion7().ToString());
            request.Headers.Add(TestAuthHandler.PermissionsHeader, permissions);
        }

        return _client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Publishing_without_a_token_is_a_401()
    {
        // No X-Test-User header, so TestAuthHandler returns NoResult and the
        // challenge stands.
        //
        // §12.4 calls this "the test that catches UseAuthentication being
        // dropped from the pipeline". It is not, and the claim was removed
        // from the chapter rather than restated here: commenting that line out
        // leaves every test in this class green. AuthorizationMiddleware
        // evaluates through PolicyEvaluator, which falls back to
        // context.AuthenticateAsync() whenever the policy names no schemes —
        // so authorization keeps working on its own, and what a missing
        // UseAuthentication actually costs is HttpContext.User for everything
        // downstream that reads it. AuthenticationMiddlewareTests asserts that
        // half.
        //
        // What this one does catch is the policy being dropped from the
        // endpoint, which is the commoner edit and the one a reviewer skims
        // past.
        HttpResponseMessage response = await PostAsync(
            new { Name = "Walnut desk", ThumbnailUrl = (string?)null, Amount = 10m, Currency = "EUR" },
            permissions: null);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        int rows = await fixture.ScalarAsync<int>("SELECT Value = COUNT(*) FROM catalog.Products");
        rows.ShouldBe(0, "a refused request must not reach the handler");
    }

    [Fact]
    public async Task Publishing_with_the_wrong_permission_is_a_403()
    {
        // Authenticated, and carrying a permission that is not the one this
        // endpoint requires — the case a fixture that grants everything hides.
        // catalog:read is deliberately a permission no policy in this service
        // registers (CatalogPermissions has one entry): the caller is real, the
        // grant is real, and it is simply not this grant.
        HttpResponseMessage response = await PostAsync(
            new { Name = "Walnut desk", ThumbnailUrl = (string?)null, Amount = 10m, Currency = "EUR" },
            permissions: "catalog:read");

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        int rows = await fixture.ScalarAsync<int>("SELECT Value = COUNT(*) FROM catalog.Products");
        rows.ShouldBe(0, "a refused request must not reach the handler");

        // Which handler answered it, because CatalogApiFactory makes a claim
        // about that and the status code cannot tell them apart — a bare 403 is
        // what the bearer handler and the test one both produce.
        // DefaultForbidScheme is unset and the provider falls back to
        // DefaultChallengeScheme before DefaultScheme, so the test scheme
        // answers, which is why this needs no reachable authority.
        IAuthenticationSchemeProvider schemes =
            fixture.Factory.Services.GetRequiredService<IAuthenticationSchemeProvider>();

        AuthenticationScheme? forbid = await schemes.GetDefaultForbidSchemeAsync();

        forbid?.Name.ShouldBe(TestAuthHandler.SchemeName);
    }

    [Fact]
    public async Task The_listing_is_reachable_without_a_token()
    {
        // §10.2's catalog-public route is GET-only and carries no
        // AuthorizationPolicy, so the group's RequireAuthorization must not
        // reach this endpoint. Over the wire is the only place that is visible:
        // AllowAnonymous is metadata, and metadata that fails to suppress the
        // group's policy looks identical to metadata that succeeds until a
        // request without a token arrives.
        HttpResponseMessage response = await _client.GetAsync(
            "/v1/catalog/products",
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

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
    public async Task Publishing_without_an_amount_is_a_400_not_a_free_product()
    {
        // A bare decimal cannot say "absent": an omitted amount would bind as
        // 0 and publish a free product indistinguishable from a deliberate
        // one. The command's nullable Amount plus the validator's NotNull
        // turn the omission into the field-keyed 400 every other bad field
        // gets — and only this boundary can see the omission at all.
        // Authorised, so the 400 is the validator's answer and not the
        // pipeline's: an unauthenticated request would 401 here and read as
        // this assertion passing on the wrong grounds.
        HttpResponseMessage response = await PostAsync(
            new { Name = "Walnut desk", Currency = "EUR" },
            CatalogPermissions.Write);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.ShouldContain("Amount");

        int rows = await fixture.ScalarAsync<int>("SELECT Value = COUNT(*) FROM catalog.Products");
        rows.ShouldBe(0);
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
