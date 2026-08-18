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
/// <b>There is no rebuild path, and §6.6 names the one that is owed.</b> This
/// service holds no source of truth for prices, so it cannot rebuild the table
/// from anything of its own — the procedure is Catalog republishing its
/// catalogue, which does not exist yet. Until it does, every product published
/// before <c>ordering-catalog-events</c> was first declared is absent, because
/// the broker drops what no queue is bound for, and each is an order refused
/// with no fault anywhere. §6.6 also records the constraint that republish has
/// to meet: it must carry each product's original <c>OccurredAt</c>, since a
/// fresh one sails past the withdrawal watermark below and re-lists everything
/// Catalog ever discontinued.
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
    /// §6.6's upsert, both hints and all. <c>WITH (HOLDLOCK)</c> appears twice
    /// and guards two different things — a concurrent insert of the same
    /// price key, and a concurrent withdrawal of the product — and
    /// <see cref="UpsertAsync"/>'s remarks argue both.
    /// </summary>
    private const string UpsertSql =
        """
        SET XACT_ABORT ON;
        BEGIN TRANSACTION;

        -- The watermark is read FIRST, and under HOLDLOCK, and both halves are
        -- load-bearing. A withdrawal newer than this event means Catalog has
        -- since pulled the product, so the row this statement is about to
        -- write is not orderable — whether or not a row for this currency
        -- existed when the withdrawal ran.
        --
        -- HOLDLOCK because the interesting answer is an ABSENCE: at read
        -- committed the lock is released immediately, so a discontinuation can
        -- commit between this read and the insert below and leave a withdrawn
        -- product available. A key-range lock on this ProductId is what makes
        -- "no withdrawal" hold until COMMIT. HOLDLOCK on ProductPrices does
        -- not reach this table.
        --
        -- FIRST because the discontinue statement takes these two tables in
        -- this order as well. Same order, no cycle — the deadlock that
        -- otherwise appears the moment both statements run concurrently for
        -- one product.
        DECLARE @IsAvailable bit =
            CASE
                WHEN EXISTS (
                    SELECT 1
                    FROM ordering.ProductWithdrawals WITH (HOLDLOCK)
                    WHERE ProductId = @ProductId
                        AND WithdrawnAt >= @OccurredAt)
                THEN 0
                ELSE 1
            END;

        MERGE ordering.ProductPrices WITH (HOLDLOCK) AS target
        USING (SELECT ProductId = @ProductId, Currency = @Currency) AS source
            ON target.ProductId = source.ProductId
            AND target.Currency = source.Currency
        -- NOT MATCHED is the branch no UpdatedAt comparison can cover, because
        -- there is no target row to compare against — which is why the
        -- withdrawal watermark exists at all.
        WHEN NOT MATCHED THEN
            INSERT (ProductId, Currency, Amount, IsAvailable, UpdatedAt)
            VALUES (@ProductId, @Currency, @Amount, @IsAvailable, @OccurredAt)
        -- The out-of-order guard. At-least-once delivery (§9.4) means a
        -- redelivered ProductPublished can arrive after the PriceChanged that
        -- superseded it, and without this line the older amount wins and stays
        -- won — a wrong price on the write path, with nothing failing.
        --
        -- STRICT here, where the withdrawal comparison is not, and §6.6 argues
        -- the asymmetry: a tie between a price and a withdrawal has a business
        -- answer (only a later price re-lists), and a tie between two prices
        -- has none — the publisher said they happened at the same instant, so
        -- delivery order decides and OccurredAt is not a total order. Ranking
        -- them needs a per-product sequence in §9.1's envelope, which is a
        -- platform decision rather than this statement's.
        WHEN MATCHED AND target.UpdatedAt < @OccurredAt THEN
            UPDATE SET Amount = @Amount, IsAvailable = @IsAvailable, UpdatedAt = @OccurredAt;

        COMMIT;
        """;

    /// <summary>
    /// §6.6's discontinue, in two halves: a product-level watermark, and the
    /// per-currency rows that already exist. Every currency for the product,
    /// because <c>ProductDiscontinued</c> carries none — a product is
    /// withdrawn whole or not at all.
    /// </summary>
    /// <remarks>
    /// <c>IsAvailable = 0</c> rather than a <c>DELETE</c>, which is §6.6's
    /// decision and worth restating where the statement is: an order placed
    /// last month has to stay explicable, and a row that vanishes takes its
    /// price with it. The reader filters on the flag, so the customer meets
    /// the same <c>ProductsUnavailable</c> either way.
    /// <para>
    /// <b>The <c>UPDATE</c> alone was wrong, and §6.6 printed it that way.</b>
    /// It reaches only the rows that exist when it runs, so a withdrawal
    /// claimed ahead of a still-retrying publish (§9.4 guarantees no ordering)
    /// touched nothing, and the publish then took the price <c>MERGE</c>'s
    /// <c>NOT MATCHED</c> branch and inserted an orderable row for a
    /// discontinued product. A stale price for a currency the withdrawal never
    /// saw does the same with no reordering at all. Both are covered by
    /// <see cref="Persistence.ProductWithdrawal"/>, which the upsert consults on exactly
    /// that branch; both were reproduced as failing tests before this was
    /// written.
    /// </para>
    /// <para>
    /// <b>One transaction, because the two halves are one fact.</b> The
    /// watermark without the rows leaves existing prices orderable; the rows
    /// without the watermark leaves the hole this fixes. At-least-once
    /// redelivery would repair either, but a message that exhausts its retries
    /// (§9.8) would not, and <c>SET XACT_ABORT ON</c> is what makes a mid-batch
    /// failure roll the first statement back rather than leave half of it
    /// standing.
    /// </para>
    /// </remarks>
    private const string DiscontinueSql =
        """
        SET XACT_ABORT ON;
        BEGIN TRANSACTION;

        -- The watermark first, because it is the half that must survive having
        -- no price row to write to. Monotonic: a redelivered or stale
        -- withdrawal must not move it backwards over a later one.
        MERGE ordering.ProductWithdrawals WITH (HOLDLOCK) AS target
        USING (SELECT ProductId = @ProductId) AS source
            ON target.ProductId = source.ProductId
        WHEN NOT MATCHED THEN
            INSERT (ProductId, WithdrawnAt)
            VALUES (@ProductId, @OccurredAt)
        WHEN MATCHED AND target.WithdrawnAt < @OccurredAt THEN
            UPDATE SET WithdrawnAt = @OccurredAt;

        -- Then the rows that already exist. The watermark covers the ones that
        -- do not, so between them every currency is reached.
        UPDATE ordering.ProductPrices
        SET IsAvailable = 0, UpdatedAt = @OccurredAt
        WHERE ProductId = @ProductId
            AND UpdatedAt <= @OccurredAt;

        COMMIT;
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
    /// <b><c>WITH (HOLDLOCK)</c> is what makes this statement safe under
    /// concurrent delivery, and §6.6 prints it because PR-20 amended the
    /// chapter to.</b> A bare <c>MERGE</c> takes no range
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
    /// <b>The watermark read carries the same hint, for a different absence.</b>
    /// The upsert's second guard asks whether a withdrawal exists, and at read
    /// committed the interesting answer — <em>no</em> — is protected by
    /// nothing: a discontinuation can commit between that read and the insert,
    /// and the product goes back on sale. <c>HOLDLOCK</c> on
    /// <c>ProductPrices</c> does not reach <c>ProductWithdrawals</c>, so the
    /// read takes its own, inside a transaction, and takes it <em>first</em> —
    /// the order the discontinue statement already uses, which is what keeps
    /// the two from deadlocking against each other. Copilot found this one, in
    /// the fix for the bug it had found the round before.
    /// </para>
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
    /// <b>The currency is upper-cased here, and on the read side too.</b>
    /// Nothing between Catalog's <c>Money</c> and this statement normalises
    /// anything: <c>Currency</c> crosses the wire as a <c>string</c> like any
    /// other, so the value that reaches this parameter is whatever the
    /// publisher put in the contract. Under a case-sensitive collation an
    /// unnormalised one writes a row <c>ProjectedPriceReader</c> cannot find,
    /// and a second primary-key row beside the one it can — so both sides
    /// upper-case, and neither is redundant.
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
