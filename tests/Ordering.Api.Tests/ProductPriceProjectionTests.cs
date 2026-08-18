using Common.Application;
using Common.Contracts.Catalog.V1;
using Ordering.Application.Orders;
using Ordering.Domain.Common;
using Ordering.Domain.Orders;
using Ordering.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Ordering.Api.Tests;

/// <summary>
/// §6.6's price projection against the real table, driven through the handler
/// interfaces the §6.2 scan registered it under. The broker is not in this
/// suite deliberately — what these tests are about is the statement's
/// arithmetic, and a MERGE's guard is a property of SQL Server rather than of
/// how the message arrived. <see cref="CatalogEventEndpointTests"/> is the
/// other half, and drives the same handler over a real queue.
/// </summary>
/// <remarks>
/// <b>Resolved, never constructed.</b> The projection is registered by
/// <c>AddPluggableFrom</c> alone (§6.2), so a test that did
/// <c>new ProductPriceProjection(factory)</c> would keep passing with the
/// class made internal — which is the one change that silently unregisters it
/// and leaves every delivery reaching §9.4's throw.
/// </remarks>
[Collection(nameof(IntegrationCollection))]
public sealed class ProductPriceProjectionTests(ServiceFixture fixture) : IAsyncLifetime
{
    /// <summary>
    /// Fixed instants rather than <c>UtcNow</c>, because every assertion here
    /// is about which of two timestamps is larger. A clock would make the
    /// guard tests pass for the reason they are meant to and also for the
    /// reason that two calls a millisecond apart are ordered anyway.
    /// </summary>
    private static readonly DateTimeOffset Published = new(2026, 8, 1, 9, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset Later = Published.AddHours(1);

    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task A_published_product_becomes_an_available_row_at_its_price()
    {
        Guid product = Guid.CreateVersion7();

        await HandleAsync(Publish(product, 19.99m, "EUR", Published));

        (await AmountAsync(product)).ShouldBe(19.99m);
        (await IsAvailableAsync(product)).ShouldBeTrue(
            "the insert branch writes IsAvailable = 1 rather than leaning on the column default, so a " +
            "product republished after being discontinued comes back");
        (await UpdatedAtAsync(product)).ShouldBe(Published);
    }

    [Fact]
    public async Task The_same_event_delivered_twice_leaves_one_row_untouched()
    {
        // At-least-once is what the broker promises (§9.4), so this is the
        // ordinary case rather than the pathological one. The redelivery takes
        // the MATCHED branch and its guard refuses it — OccurredAt is not
        // strictly greater than the UpdatedAt the first delivery wrote — so
        // the row is not even rewritten with identical values.
        Guid product = Guid.CreateVersion7();
        ProductPublished published = Publish(product, 19.99m, "EUR", Published);

        await HandleAsync(published);
        await HandleAsync(published);

        (await RowCountAsync(product)).ShouldBe(1);
        (await AmountAsync(product)).ShouldBe(19.99m);
    }

    [Fact]
    public async Task A_newer_price_replaces_an_older_one()
    {
        Guid product = Guid.CreateVersion7();
        await HandleAsync(Publish(product, 19.99m, "EUR", Published));

        await HandleAsync(PriceOf(product, 24.99m, "EUR", Later));

        (await AmountAsync(product)).ShouldBe(24.99m);
        (await UpdatedAtAsync(product)).ShouldBe(Later);
    }

    [Fact]
    public async Task A_stale_price_does_not_overwrite_a_newer_one()
    {
        // The out-of-order guard, and the failure it prevents is silent: a
        // redelivered PriceChanged arriving behind the one that superseded it
        // would put yesterday's amount on the write path with nothing throwing
        // and nothing logged.
        Guid product = Guid.CreateVersion7();
        await HandleAsync(Publish(product, 24.99m, "EUR", Later));

        await HandleAsync(PriceOf(product, 19.99m, "EUR", Published));

        (await AmountAsync(product)).ShouldBe(
            24.99m,
            "the MATCHED branch fires only when target.UpdatedAt < @OccurredAt, so the older event is a " +
            "no-op rather than the last writer");
        (await UpdatedAtAsync(product)).ShouldBe(Later);
    }

    [Fact]
    public async Task A_discontinued_product_stops_being_readable_without_losing_its_price()
    {
        Guid product = Guid.CreateVersion7();
        await HandleAsync(Publish(product, 19.99m, "EUR", Published));

        await HandleAsync(Discontinue(product, Later));

        (await IsAvailableAsync(product)).ShouldBeFalse();
        (await AmountAsync(product)).ShouldBe(
            19.99m,
            "§6.6 flags rather than deletes, because an order placed last month has to stay explicable");
        (await ReadPriceAsync(product, "EUR")).ShouldBeNull(
            "the reader filters on IsAvailable, so the customer meets the same ProductsUnavailable as for " +
            "a product that was never published");
    }

    [Fact]
    public async Task A_stale_discontinue_does_not_withdraw_a_newer_price()
    {
        // The same guard as the MERGE's, on the statement that does not have a
        // MERGE. A copy per event is how one of the two ends up without it,
        // which is why this test exists beside the one above rather than
        // trusting the pair to be written alike.
        Guid product = Guid.CreateVersion7();
        await HandleAsync(Publish(product, 19.99m, "EUR", Later));

        await HandleAsync(Discontinue(product, Published));

        (await IsAvailableAsync(product)).ShouldBeTrue();
    }

    [Fact]
    public async Task A_price_published_after_a_withdrawal_makes_the_product_orderable_again()
    {
        Guid product = Guid.CreateVersion7();
        await HandleAsync(Publish(product, 19.99m, "EUR", Published));
        await HandleAsync(Discontinue(product, Later));

        await HandleAsync(PriceOf(product, 29.99m, "EUR", Later.AddHours(1)));

        (await IsAvailableAsync(product)).ShouldBeTrue(
            "IsAvailable = 1 on the update branch is what makes re-listing a product a price event rather " +
            "than an operator's UPDATE");
        (await ReadPriceAsync(product, "EUR")).ShouldBe(Money.Of(29.99m, "EUR"));
    }

    [Fact]
    public async Task A_withdrawal_covers_every_currency_the_product_is_priced_in()
    {
        // ProductDiscontinued carries no currency, so the statement keys on
        // the product alone — a product is withdrawn whole or not at all.
        Guid product = Guid.CreateVersion7();
        await HandleAsync(Publish(product, 19.99m, "EUR", Published));
        await HandleAsync(PriceOf(product, 17.99m, "GBP", Published));

        await HandleAsync(Discontinue(product, Later));

        (await RowCountAsync(product)).ShouldBe(2);
        (await ReadPriceAsync(product, "EUR")).ShouldBeNull();
        (await ReadPriceAsync(product, "GBP")).ShouldBeNull();
    }

    [Fact]
    public async Task A_withdrawal_that_arrives_before_any_price_still_withdraws_the_product()
    {
        // §9.4 guarantees no ordering, so a ProductDiscontinued can be claimed
        // ahead of the ProductPublished that is still retrying behind it. The
        // discontinue statement matches no row and the publish then takes the
        // MERGE's NOT MATCHED branch — which is the one branch no guard
        // covers, because there is no target row whose UpdatedAt it could
        // compare against.
        //
        // §6.6 already names this exact shape one projection over: an UPDATE
        // for OrderSummaries' status events "would be the whole defect …  a
        // Cancelled claimed before its OrderPlaced would match no row, change
        // nothing, and be marked processed". The price table's answer has to
        // be the same — the withdrawal must survive having nothing to write to.
        Guid product = Guid.CreateVersion7();

        await HandleAsync(Discontinue(product, Later));
        await HandleAsync(Publish(product, 19.99m, "EUR", Published));

        (await IsAvailableAsync(product)).ShouldBeFalse(
            "the product was withdrawn after this price was published, so the row the late publish " +
            "creates must not be orderable — a discontinued product back on sale is the failure");
        (await ReadPriceAsync(product, "EUR")).ShouldBeNull();
    }

    [Fact]
    public async Task A_withdrawal_reaches_a_currency_it_had_never_seen_a_price_for()
    {
        // The same hole through the other door, and the one that needs no
        // out-of-order broker at all to be reachable: the withdrawal only ever
        // touched the rows that existed when it ran. A stale price for a
        // currency nobody had projected yet inserts a fresh row, and without a
        // product-level record of the withdrawal that row has nothing to
        // inherit unavailability from.
        Guid product = Guid.CreateVersion7();
        await HandleAsync(Publish(product, 19.99m, "EUR", Published));

        await HandleAsync(Discontinue(product, Later));
        await HandleAsync(PriceOf(product, 17.99m, "GBP", Published.AddMinutes(1)));

        (await ReadPriceAsync(product, "GBP")).ShouldBeNull(
            "the GBP price predates the withdrawal, so projecting it late must not make the product " +
            "orderable in a currency the withdrawal never saw");
    }

    [Fact]
    public async Task A_price_published_after_a_withdrawal_relists_a_currency_that_was_never_priced()
    {
        // The counterweight to the two above, and the reason the guard is a
        // comparison rather than a flag: a withdrawal must not make a product
        // permanently unorderable. A price genuinely newer than the withdrawal
        // re-lists it, in a currency that has no row either.
        Guid product = Guid.CreateVersion7();
        await HandleAsync(Discontinue(product, Published));

        await HandleAsync(PriceOf(product, 17.99m, "GBP", Later));

        (await ReadPriceAsync(product, "GBP")).ShouldBe(Money.Of(17.99m, "GBP"));
    }

    [Fact]
    public async Task Two_currencies_are_two_rows_rather_than_the_second_overwriting_the_first()
    {
        Guid product = Guid.CreateVersion7();

        await HandleAsync(Publish(product, 19.99m, "EUR", Published));
        await HandleAsync(PriceOf(product, 17.99m, "GBP", Published));

        (await RowCountAsync(product)).ShouldBe(
            2,
            "the source clause matches on currency as well as product, which is what makes the composite " +
            "primary key mean something");
        (await ReadPriceAsync(product, "EUR")).ShouldBe(Money.Of(19.99m, "EUR"));
        (await ReadPriceAsync(product, "GBP")).ShouldBe(Money.Of(17.99m, "GBP"));
    }

    [Fact]
    public async Task A_lower_cased_contract_currency_is_stored_upper_cased_and_the_reader_finds_it()
    {
        // The wire is a string and Catalog's Money.Of is on the other side of
        // it, so nothing between the two normalises. ProjectedPriceReader
        // upper-cases its parameter and says it does so because this column is
        // written through that normalisation — which is only true because the
        // projection does it here.
        Guid product = Guid.CreateVersion7();

        await HandleAsync(Publish(product, 19.99m, "eur", Published));

        (await CurrencyAsync(product)).ShouldBe("EUR");
        (await ReadPriceAsync(product, "EUR")).ShouldBe(Money.Of(19.99m, "EUR"));
    }

    /// <summary>
    /// Concurrent deliveries for one key converge on one row carrying the
    /// newest event's amount, whatever order they ran in.
    /// </summary>
    /// <remarks>
    /// <b>This test does NOT catch <c>WITH (HOLDLOCK)</c> being removed, and
    /// saying so is the point.</b> The hint is there because a bare
    /// <c>MERGE</c> takes no range lock over a key it failed to find, so two
    /// deliveries can both take the <c>NOT MATCHED</c> branch and the loser
    /// violates the primary key. Measured rather than assumed: with the hint
    /// deleted this passed at eight-way and again at sixty-four-way, three
    /// runs each — the window between the search and the insert is too small
    /// for a test that reaches SQL Server over a connection to land inside.
    /// <para>
    /// So the hint is a reasoned claim rather than an observed one, in the
    /// class PR-17's rate-limiter ordering row is already in, and it is kept
    /// for two reasons the measurement does not touch: the failure it prevents
    /// is repaired by the endpoint's retry (§9.8), so its absence would read
    /// as a burst of warnings rather than as a defect, and a correctness
    /// property that depends on a retry policy stops holding the day somebody
    /// tunes one. What this test does cover is the guard's outcome under
    /// concurrency, which is a different claim and a real one.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task One_product_and_currency_under_concurrent_delivery_is_still_one_row()
    {
        Guid product = Guid.CreateVersion7();

        await Task.WhenAll(
            Enumerable
                .Range(0, 8)
                .Select(i => HandleAsync(PriceOf(product, 10m + i, "EUR", Published.AddMinutes(i)))));

        (await RowCountAsync(product)).ShouldBe(1);
        (await AmountAsync(product)).ShouldBe(
            17m,
            "the guard makes the newest event the winner whatever order the eight ran in");
    }

    private static ProductPublished Publish(Guid product, decimal amount, string currency, DateTimeOffset at) =>
        new()
        {
            MessageId = Guid.CreateVersion7(),
            CorrelationId = Guid.CreateVersion7(),
            OccurredAt = at,
            ProductId = product,
            Name = "A product",
            ThumbnailUrl = null,
            Amount = amount,
            Currency = currency
        };

    private static PriceChanged PriceOf(Guid product, decimal amount, string currency, DateTimeOffset at) =>
        new()
        {
            MessageId = Guid.CreateVersion7(),
            CorrelationId = Guid.CreateVersion7(),
            OccurredAt = at,
            ProductId = product,
            Amount = amount,
            Currency = currency
        };

    private static ProductDiscontinued Discontinue(Guid product, DateTimeOffset at) =>
        new()
        {
            MessageId = Guid.CreateVersion7(),
            CorrelationId = Guid.CreateVersion7(),
            OccurredAt = at,
            ProductId = product
        };

    /// <summary>
    /// One scope per delivery, because that is what the consumer gives a
    /// handler — and the handler is scoped, so a shared scope would hand every
    /// call in a test the same instance and quietly stop covering the
    /// connection-per-call shape.
    /// </summary>
    private async Task HandleAsync<TEvent>(TEvent integrationEvent)
        where TEvent : class
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();

        await scope.ServiceProvider
            .GetRequiredService<IIntegrationEventHandler<TEvent>>()
            .HandleAsync(integrationEvent, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// The real §6.4 port over the row the projection just wrote, so the two
    /// halves of §6.6's table are asserted against each other rather than each
    /// against a copy of the schema.
    /// </summary>
    private async Task<Money?> ReadPriceAsync(Guid product, string currency)
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();

        IReadOnlyDictionary<ProductId, Money> prices = await scope.ServiceProvider
            .GetRequiredService<IProductPriceReader>()
            .GetAsync([new ProductId(product)], currency, TestContext.Current.CancellationToken);

        return prices.TryGetValue(new ProductId(product), out Money price) ? price : null;
    }

    private Task<int> RowCountAsync(Guid product) =>
        fixture.ScalarAsync<int>(
            "SELECT Value = COUNT(*) FROM ordering.ProductPrices WHERE ProductId = {0}",
            product);

    private Task<decimal> AmountAsync(Guid product) =>
        fixture.ScalarAsync<decimal>(
            "SELECT Value = Amount FROM ordering.ProductPrices WHERE ProductId = {0}",
            product);

    private Task<bool> IsAvailableAsync(Guid product) =>
        fixture.ScalarAsync<bool>(
            "SELECT Value = IsAvailable FROM ordering.ProductPrices WHERE ProductId = {0}",
            product);

    private Task<DateTimeOffset> UpdatedAtAsync(Guid product) =>
        fixture.ScalarAsync<DateTimeOffset>(
            "SELECT Value = UpdatedAt FROM ordering.ProductPrices WHERE ProductId = {0}",
            product);

    private Task<string> CurrencyAsync(Guid product) =>
        fixture.ScalarAsync<string>(
            "SELECT Value = Currency FROM ordering.ProductPrices WHERE ProductId = {0}",
            product);
}
