using System.Data;
using Common.Application;
using Common.Contracts.Catalog.V1;
using Dapper;

namespace Ordering.Infrastructure.Projections;

/// <summary>
/// §6.6's price projection: Catalog's three product events, applied to
/// <c>ordering.ProductPrices</c>. Infrastructure rather than Application
/// because it is raw SQL over a connection factory (§6.6), and registered by
/// <c>AddOrderingInfrastructure</c>'s scan (§6.2) — Application's scan would
/// not see it.
/// </summary>
/// <remarks>
/// <b>This is the read model a WRITE path depends on</b>, which is what makes
/// it the more consequential of §6.6's two:
/// <c>ProjectedPriceReader</c> serves <c>PlaceOrder</c> from it, so a row that
/// is missing is an order that is refused. §6.6's callout is the standing
/// consequence — a product Catalog has never published produces
/// <c>order.products_unavailable</c>, which is a correct answer from a service
/// with no prices and looks like nothing at all in a log.
/// <para>
/// <b>Public, and the modifier is load-bearing.</b> §6.2's scan is
/// public-only, so an internal handler is registered as nothing at all —
/// silently, with the endpoint still bound, so every delivery reaches §9.4's
/// "no handler is registered" throw instead of a table.
/// </para>
/// <para>
/// <b>Three interfaces, two of them ahead of their publisher.</b> §3.2 gives
/// Ordering all three of Catalog's events; Catalog's §9.3 allow-list maps one
/// of them today, because <c>Product</c> has no price-change or discontinue
/// method yet. Building a third of the class would leave the next PR
/// re-deciding this file's guard and its normalisation, which is §10.2's
/// dual-version trap one chapter over. A handler with no publisher costs an
/// idle queue binding; a partial projection costs a second opinion about what
/// <c>UpdatedAt</c> means.
/// </para>
/// </remarks>
public sealed class ProductPriceProjection(IDbConnectionFactory connections)
    : IIntegrationEventHandler<ProductPublished>,
      IIntegrationEventHandler<PriceChanged>,
      IIntegrationEventHandler<ProductDiscontinued>
{
    /// <summary>
    /// §6.6's upsert. <c>WITH (HOLDLOCK)</c> is this file's one addition to
    /// the printed statement, and it is a correctness fix rather than a tuning
    /// one — <see cref="UpsertAsync"/>'s remarks argue it.
    /// </summary>
    private const string UpsertSql =
        """
        MERGE ordering.ProductPrices WITH (HOLDLOCK) AS target
        USING (SELECT ProductId = @ProductId, Currency = @Currency) AS source
            ON target.ProductId = source.ProductId
            AND target.Currency = source.Currency
        WHEN NOT MATCHED THEN
            INSERT (ProductId, Currency, Amount, IsAvailable, UpdatedAt)
            VALUES (@ProductId, @Currency, @Amount, 1, @OccurredAt)
        -- The out-of-order guard. At-least-once delivery (§9.4) means a
        -- redelivered ProductPublished can arrive after the PriceChanged that
        -- superseded it, and without this line the older amount wins and stays
        -- won — a wrong price on the write path, with nothing failing.
        WHEN MATCHED AND target.UpdatedAt < @OccurredAt THEN
            UPDATE SET Amount = @Amount, IsAvailable = 1, UpdatedAt = @OccurredAt;
        """;

    /// <summary>
    /// §6.6's discontinue. Every currency for the product, because
    /// <c>ProductDiscontinued</c> carries none — a product is withdrawn whole
    /// or not at all.
    /// </summary>
    /// <remarks>
    /// <c>IsAvailable = 0</c> rather than a <c>DELETE</c>, which is §6.6's
    /// decision and worth restating where the statement is: an order placed
    /// last month has to stay explicable, and a row that vanishes takes its
    /// price with it. The reader filters on the flag, so the customer meets
    /// the same <c>ProductsUnavailable</c> either way.
    /// </remarks>
    private const string DiscontinueSql =
        """
        UPDATE ordering.ProductPrices
        SET IsAvailable = 0, UpdatedAt = @OccurredAt
        WHERE ProductId = @ProductId
            AND UpdatedAt < @OccurredAt;
        """;

    public Task HandleAsync(ProductPublished integrationEvent, CancellationToken ct) =>
        UpsertAsync(
            integrationEvent.ProductId,
            integrationEvent.Currency,
            integrationEvent.Amount,
            integrationEvent.OccurredAt,
            ct);

    public Task HandleAsync(PriceChanged integrationEvent, CancellationToken ct) =>
        UpsertAsync(
            integrationEvent.ProductId,
            integrationEvent.Currency,
            integrationEvent.Amount,
            integrationEvent.OccurredAt,
            ct);

    public Task HandleAsync(ProductDiscontinued integrationEvent, CancellationToken ct) =>
        ExecuteAsync(
            DiscontinueSql,
            new { integrationEvent.ProductId, integrationEvent.OccurredAt },
            ct);

    /// <summary>
    /// One statement for both price-bearing events, because they differ only
    /// in which fact moved the amount — and because a copy per event is how
    /// one of the two ends up without the guard (§6.6 makes the same argument
    /// about <c>OrderSummaries</c>' status transitions).
    /// </summary>
    /// <remarks>
    /// <b><c>WITH (HOLDLOCK)</c> is not in §6.6's printed statement, and the
    /// chapter was amended to carry it.</b> A bare <c>MERGE</c> takes no range
    /// lock over the key it failed to find, so two deliveries for one
    /// <c>(ProductId, Currency)</c> can both take the <c>NOT MATCHED</c>
    /// branch and the second insert violates the primary key — and the
    /// endpoint sets no <c>ConcurrentMessageLimit</c>, so deliveries can
    /// overlap and that is an ordinary Tuesday rather than a contrived race.
    /// The endpoint's retry (§9.8) would absorb it on
    /// the second attempt, which is exactly why it is worth closing here: a
    /// correctness property that happens to be repaired by a retry policy is
    /// one that stops holding the day somebody tunes the retry policy.
    /// <para>
    /// <b>No test catches the hint being deleted, and that was measured
    /// rather than assumed.</b> Removing it left
    /// <c>ProductPriceProjectionTests</c> green at eight-way and again at
    /// sixty-four-way concurrency, three runs each: the window between the
    /// search and the insert is smaller than a test can aim at over a
    /// connection. So this is a reasoned claim rather than an observed one,
    /// which is the class PR-17's rate-limiter ordering row is already in —
    /// and the test says so in its own remarks rather than looking like the
    /// guard it is not.
    /// </para>
    /// <para>
    /// <b>The currency is upper-cased on the way in, and the reader already
    /// promised that it would be.</b> <c>ProjectedPriceReader</c> upper-cases
    /// its parameter and says so, on the grounds that this column is written
    /// through <c>Money.Of</c>'s normalisation — which is true of Catalog's
    /// <c>Money</c> and not of the wire, where <c>Currency</c> is a
    /// <c>string</c> like any other. Under a case-sensitive collation a
    /// lower-cased contract would produce a row the reader cannot find, and a
    /// second primary-key row beside the one it can, so the normalisation
    /// belongs on the side that writes as much as on the side that reads.
    /// </para>
    /// </remarks>
    private Task UpsertAsync(
        Guid productId,
        string currency,
        decimal amount,
        DateTimeOffset occurredAt,
        CancellationToken ct) =>
        ExecuteAsync(
            UpsertSql,
            new
            {
                ProductId = productId,
                Currency = currency.ToUpperInvariant(),
                Amount = amount,
                OccurredAt = occurredAt
            },
            ct);

    /// <summary>
    /// Its own connection, never the consumer's <c>DbContext</c> — §6.6 and
    /// §7.5 both say a projection must not run inside the write transaction,
    /// and this one does not run inside one at all: it is reached from the
    /// broker, after Catalog committed, on a connection of its own.
    /// </summary>
    private async Task ExecuteAsync(string sql, object parameters, CancellationToken ct)
    {
        using IDbConnection connection = connections.Create();

        await connection.ExecuteAsync(new CommandDefinition(sql, parameters, cancellationToken: ct));
    }
}
