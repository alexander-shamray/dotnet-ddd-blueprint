using Catalog.TestSupport;
using Catalog.TestSupport.Outbox;
using Common.Application;
using Common.Infrastructure.Idempotency;
using Common.Infrastructure.Inbox;
using Common.Infrastructure.Messaging;
using Common.Infrastructure.Outbox;
using Shouldly;
using Xunit;

namespace Catalog.Api.Tests;

/// <summary>
/// §9.4's, §9.5's and §8.5's retention purges, driven a pass at a time against
/// the real tables. The predicate that separates them is the whole subject: the
/// outbox deletes on <c>ProcessedAt IS NOT NULL</c> <em>and</em> age, the inbox
/// on age alone, and the marker on age <em>and</em> the claim store having let
/// its key go — age selects there and no longer decides (ADR-039). Getting the
/// outbox's wrong is silent, permanent data loss; getting the marker's wrong is
/// a duplicate write.
/// </summary>
[Collection(nameof(IntegrationCollection))]
public sealed class RetentionPurgeTests(ServiceFixture fixture) : IAsyncLifetime
{
    // Comfortably past the seven-day window either side, so the test is about
    // the predicate rather than about arithmetic near a boundary.
    private static DateTimeOffset LongAgo => DateTimeOffset.UtcNow.AddDays(-30);
    private static DateTimeOffset Recently => DateTimeOffset.UtcNow.AddDays(-1);

    /// <summary>
    /// A key in §8.5's shape — {subject}:{operation}:{commandId} — distinct per
    /// call, because the column is the primary key and two markers staged in
    /// one test must be two rows.
    /// </summary>
    private static string Key() =>
        $"{Guid.CreateVersion7()}:tests.purge:{Guid.CreateVersion7()}";

    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task A_processed_outbox_row_past_the_window_is_deleted()
    {
        OutboxMessage row = OutboxRows.Healthy(fixture);
        await fixture.StageOutboxAsync(row);
        await fixture.SetOutboxProcessedAtAsync(row.MessageId, LongAgo);

        (await fixture.PurgeRetentionAsync()).Outbox.ShouldBe(1);

        (await fixture.OutboxAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task An_abandoned_outbox_row_survives_however_old_it_is()
    {
        // The reason `ProcessedAt IS NOT NULL` is load-bearing rather than
        // defensive (§9.4). An abandoned row — attempts at the cap, never
        // processed — is exactly what §13.6's alert exists to surface, and
        // purging on age alone would turn permanent data loss into a clean,
        // empty table. Nothing else in the system would notice.
        OutboxMessage poison = OutboxRows.Poison(fixture);
        await fixture.StageOutboxAsync(poison);
        await fixture.SetOutboxAttemptsAsync(poison.MessageId, 10);

        // OccurredAt, deliberately — and NOT the column the purge reads, which
        // is the whole point. `ProcessedAt` is null on an abandoned row by
        // definition, so the predicate can never match it and no ageing of that
        // column is possible. What this line does is make the row old by the
        // one measure a *wrong* purge would use: written `WHERE OccurredAt <
        // @Before`, the age-alone form §9.4 warns about, this row is thirty days
        // past the window and would be deleted. That is the mutation the
        // assertion below has to be able to fail on, and without this line it
        // could not — the row would be inside every window and survive a
        // correct purge and an incorrect one alike.
        await fixture.ExecuteAsync(
            "UPDATE catalog.OutboxMessages SET OccurredAt = {0} WHERE MessageId = {1};",
            LongAgo,
            poison.MessageId);

        (await fixture.PurgeRetentionAsync()).Outbox.ShouldBe(0);

        OutboxMessage survivor = (await fixture.OutboxAsync()).ShouldHaveSingleItem();
        survivor.MessageId.ShouldBe(poison.MessageId);
        survivor.ProcessedAt.ShouldBeNull();
    }

    [Fact]
    public async Task A_processed_outbox_row_inside_the_window_survives()
    {
        // Processed rows are kept for a few days for debugging (§9.4), so the
        // window has to be read as well as the null check — a purge matching on
        // the predicate alone would delete yesterday's evidence.
        OutboxMessage row = OutboxRows.Healthy(fixture);
        await fixture.StageOutboxAsync(row);
        await fixture.SetOutboxProcessedAtAsync(row.MessageId, Recently);

        (await fixture.PurgeRetentionAsync()).Outbox.ShouldBe(0);

        (await fixture.OutboxAsync()).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Two_endpoints_differing_only_by_case_are_two_rows()
    {
        // The composite key is only once-per-endpoint if the database agrees
        // with the broker about what two endpoints are. SQL Server's default
        // collation is case-insensitive and a queue name is not, so `orders`
        // and `Orders` would collide — and the second endpoint's message would
        // be dropped as a duplicate of a delivery it never received. The
        // column is `Latin1_General_BIN2` for exactly this.
        //
        // Written here rather than through the filter because what is under
        // test is the key's comparison semantics: two inserts that differ in
        // one character's case must both survive.
        var messageId = Guid.CreateVersion7();

        // Two calls, so two contexts. One would put both rows in a single
        // change tracker, and EF's in-memory identity map answers a different
        // question from the one under test — what the *database* considers a
        // duplicate key is what decides whether a second endpoint's message
        // survives, and the filter only ever adds one row per consume anyway.
        await fixture.StageInboxAsync(new InboxMessage(messageId, "catalog-orders", Recently));
        await fixture.StageInboxAsync(new InboxMessage(messageId, "catalog-Orders", Recently));

        (await fixture.InboxAsync()).Count.ShouldBe(
            2,
            "case-insensitive collation would have made the second insert a primary-key violation");
    }

    [Fact]
    public async Task Two_endpoints_differing_outside_the_code_page_are_two_rows()
    {
        // The other half of the same guarantee, and the half a binary collation
        // cannot give: the collation decides how stored values compare, and this
        // is about what gets stored at all. AMQP 0-9-1 allows 255 bytes of UTF-8
        // in a queue name, so the column has to be nvarchar — under varchar,
        // every character outside the code page becomes `?`, and two endpoints
        // that differ only there arrive as the same key.
        //
        // Cyrillic, and the choice is not decoration — it is what makes this
        // test fail for the right reason. The first draft used `ő` and `ū`,
        // reasoning that anything outside the code page becomes `?`; SQL Server
        // does not do that. It **best-fit folds** what it can, so those two
        // arrived as `catalog-o` and `catalog-u`: two distinct rows, silently
        // wrong, and the count assertion below stayed green over the defect it
        // was written to catch. `ж` and `д` have no Latin form to fold to, so
        // both become `catalog-?` and the second insert is a key violation.
        //
        // Both assertions are kept because the two failures are different.
        // Folding corrupts the value without colliding; a character with no
        // fallback collides. Either one loses a message, and only one of them
        // moves the count.
        var messageId = Guid.CreateVersion7();

        await fixture.StageInboxAsync(new InboxMessage(messageId, "catalog-ж", Recently));
        await fixture.StageInboxAsync(new InboxMessage(messageId, "catalog-д", Recently));

        IReadOnlyList<InboxMessage> rows = await fixture.InboxAsync();

        rows.Count.ShouldBe(
            2,
            "varchar folds both endpoints to 'catalog-?', so the second insert is a " +
            "primary-key violation — and a message dropped by the mechanism that exists " +
            "to drop only duplicates");

        // What the column owes the filter is the endpoint it was handed,
        // unchanged. This is the assertion that catches the folding case, which
        // the count cannot see.
        rows.Select(r => r.Endpoint).ShouldBe(["catalog-ж", "catalog-д"], ignoreOrder: true);
    }

    [Fact]
    public async Task An_inbox_row_is_purged_on_age_alone()
    {
        // The asymmetry with the outbox, and it is deliberate: an inbox row
        // records that a message was handled, so there is no unfinished state
        // for a predicate to protect. What protects it is the window, which
        // §9.5 says must outlast the broker's longest redelivery — prune sooner
        // and a late redelivery arrives looking new.
        await fixture.StageInboxAsync(
            new InboxMessage(Guid.CreateVersion7(), "catalog-inventory-events", LongAgo),
            new InboxMessage(Guid.CreateVersion7(), "catalog-inventory-events", Recently));

        (await fixture.PurgeRetentionAsync()).Inbox.ShouldBe(1);

        InboxMessage survivor = (await fixture.InboxAsync()).ShouldHaveSingleItem();
        survivor.HandledAt.ShouldBeGreaterThan(LongAgo);
    }

    [Fact]
    public async Task An_unclaimed_marker_past_its_window_is_purged_and_a_recent_one_is_not()
    {
        // The window half of the predicate, isolated. Neither key here was ever
        // claimed, so the store reports both unheld and what separates them is
        // age alone — which is a statement about this test's staging, not about
        // the pass: since ADR-039 a marker goes only when it is past its window
        // AND its claim is gone. The test above it supplies the other half.
        //
        // Two rows rather than one, which is the whole point. The combined pass
        // below stages a single already-old marker, so a DELETE with no WHERE —
        // or one that ignored CommittedAt — would satisfy it: every row it is
        // given is purgeable. This is the test that fails when the window drops
        // out of the predicate, and the recent row is what makes it one.
        await fixture.StageIdempotencyMarkersAsync(
            new IdempotencyMarker(Key(), LongAgo),
            new IdempotencyMarker(Key(), Recently));

        (await fixture.PurgeRetentionAsync()).Idempotency.ShouldBe(1);

        IdempotencyMarker survivor = (await fixture.IdempotencyMarkersAsync()).ShouldHaveSingleItem();
        survivor.CommittedAt.ShouldBeGreaterThan(LongAgo);
    }

    [Fact]
    public async Task A_marker_whose_claim_is_still_held_survives_however_old_the_row_is()
    {
        // #171, staged from the only side a test can reach it. The failure was
        // a forward step of the DATABASE's clock relative to Redis's, and no
        // test here owns the container's clock — but the step's whole effect is
        // that a row reads as past its window while the claim behind it is
        // still live, and a row staged thirty days old under a live claim is
        // that state arrived at from the other end.
        //
        // Age alone deletes this row: A_marker_is_purged_on_age_alone stages
        // the same LongAgo and asserts it goes. What keeps it is the pass
        // asking the store that owns the claim (ADR-039).
        string key = Key();

        await fixture.StageIdempotencyMarkersAsync(new IdempotencyMarker(key, LongAgo));

        string? claim = await fixture.IdempotencyClaims.TryClaimAsync(
            key,
            IdempotencyRetention.Window,
            TestContext.Current.CancellationToken);

        claim.ShouldNotBeNull("the key is this test's own, so nothing else can be holding it");

        (await fixture.PurgeRetentionAsync()).Idempotency.ShouldBe(
            0,
            "the claim behind this key is still live, so the row that refuses its retry may not go");

        // The table as well as the count, because a pass that deleted the row
        // and miscounted is a different defect from one that kept it.
        (await fixture.IdempotencyMarkersAsync()).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task The_same_row_goes_once_the_claim_behind_it_has_been_released()
    {
        // The companion, and what makes the test above about the claim rather
        // than about anything else that might keep a row. Same row, same age,
        // same pass; the one thing that differs is whether the store still
        // holds the key. Without it, a pass that had simply stopped deleting
        // markers would satisfy the assertion above and nothing would notice —
        // a gate that stops covering what it claims to, which is this
        // repository's most-repeated failure.
        string key = Key();

        await fixture.StageIdempotencyMarkersAsync(new IdempotencyMarker(key, LongAgo));

        string? claim = await fixture.IdempotencyClaims.TryClaimAsync(
            key,
            IdempotencyRetention.Window,
            TestContext.Current.CancellationToken);

        claim.ShouldNotBeNull();

        await fixture.IdempotencyClaims.ReleaseAsync(
            key,
            claim,
            TestContext.Current.CancellationToken);

        (await fixture.PurgeRetentionAsync()).Idempotency.ShouldBe(1);

        (await fixture.IdempotencyMarkersAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_skewed_clock_purges_the_outbox_and_the_inbox_and_leaves_the_marker()
    {
        // The property #167 exists to establish, and the one thing the rest of
        // this suite cannot see. Every other test here stages rows against
        // DateTimeOffset.UtcNow while the test host's clock and the container's
        // agree, so all three statements read what is effectively one clock —
        // and a marker statement that had regressed to the application-supplied
        // `@Before` the other two still use would pass every one of them
        // unchanged. Nothing would go red, and ADR-038's whole decision would be
        // untested.
        //
        // So: two clocks, one age, opposite outcomes. The registered
        // TimeProvider runs two days fast, the window is one day, and three rows
        // of the same real age go into the three tables. The outbox's and the
        // inbox's cutoffs are `now - window` on that skewed clock, so their rows
        // are a day past a window they are seconds into and both are deleted.
        // The marker's cutoff is DATEADD over SYSDATETIMEOFFSET() on the server,
        // which nothing substituted in this process can reach, so it survives.
        //
        // The window is read rather than restated: RetentionPolicy refuses an
        // idempotency window below §8.5's claim, so this is the smallest one the
        // test may ask for — and the skew is twice it rather than some margin
        // over it, so the outbox and inbox rows clear their windows by a whole
        // window and nothing here sits near a boundary.
        TimeSpan window = IdempotencyRetention.MarkerFloor;

        RetentionPolicy oneDay = new()
        {
            OutboxWindow = window,
            InboxWindow = window,
            IdempotencyWindow = window
        };

        // One instant for all three rows, so what the assertions below vary is
        // which clock a statement read and nothing else.
        DateTimeOffset justNow = DateTimeOffset.UtcNow;

        OutboxMessage row = OutboxRows.Healthy(fixture);
        await fixture.StageOutboxAsync(row);
        await fixture.SetOutboxProcessedAtAsync(row.MessageId, justNow);

        await fixture.StageInboxAsync(
            new InboxMessage(Guid.CreateVersion7(), "catalog-inventory-events", justNow));

        await fixture.StageIdempotencyMarkersAsync(new IdempotencyMarker(Key(), justNow));

        (int outbox, int inbox, int idempotency) =
            await fixture.PurgeWithSkewedClockAsync(oneDay, window * 2);

        outbox.ShouldBe(1, "the outbox cutoff is subtracted from the registered clock, which is two days ahead");
        inbox.ShouldBe(1, "§9.5 keeps the inbox on that same application-computed cutoff, deliberately");
        idempotency.ShouldBe(
            0,
            "the marker's cutoff is DATEADD over SYSDATETIMEOFFSET(), so skewing this process's clock " +
            "cannot move it — a regression to @Before deletes this row, and this line is what says so " +
            "(ADR-038)");

        // The table as well as the count, because a pass that deleted the row
        // and miscounted is a different defect from one that kept it.
        (await fixture.IdempotencyMarkersAsync()).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task A_backlog_larger_than_one_batch_drains_over_batches_and_stops_at_the_ceiling()
    {
        // Two claims the single-row tests above could not make, because one row
        // never reaches a second batch: that the loop continues while a batch
        // comes back full, and that it stops at MaxBatchesPerPass rather than
        // running until the table is empty. The plan for this PR asked for the
        // first and the review noticed neither was covered.
        //
        // A policy of its own rather than the registered one: five rows against
        // a batch of two makes both edges observable in a test that stays fast,
        // where the real 5,000 would need 10,001 rows to show the same thing.
        for (int row = 0; row < 5; row++)
        {
            OutboxMessage processed = OutboxRows.Healthy(fixture);
            await fixture.StageOutboxAsync(processed);
            await fixture.SetOutboxProcessedAtAsync(processed.MessageId, LongAgo);
        }

        RetentionPolicy twoAtATime = new() { BatchSize = 2, MaxBatchesPerPass = 2 };

        // Four of five: two batches of two, then the ceiling. A loop with no
        // ceiling would return five here and hold its connection until the
        // table was empty, which is the behaviour a first run against a service
        // that has never purged must not have.
        (await fixture.PurgeWithAsync(twoAtATime)).Outbox.ShouldBe(4);
        (await fixture.OutboxAsync()).Count.ShouldBe(1);

        // The next pass takes the remainder and stops short of its ceiling,
        // because a batch that comes back under BatchSize means the table is
        // drained — which is the loop's other exit, and the one that keeps an
        // idle service from running twenty statements an hour for nothing.
        (await fixture.PurgeWithAsync(twoAtATime)).Outbox.ShouldBe(1);
        (await fixture.OutboxAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_pass_purges_every_table()
    {
        // §9.5 asks for one hosted service covering all of them, and the
        // alternative is a schedule each with one of them being the one nobody
        // notices has stopped. Asserting the set in one pass is what that
        // costs — and the third table joined it with §8.5's durable marker,
        // whose rows are the only ones here that carry a correctness property
        // rather than a debugging record.
        OutboxMessage row = OutboxRows.Healthy(fixture);
        await fixture.StageOutboxAsync(row);
        await fixture.SetOutboxProcessedAtAsync(row.MessageId, LongAgo);

        await fixture.StageInboxAsync(
            new InboxMessage(Guid.CreateVersion7(), "catalog-inventory-events", LongAgo));

        await fixture.StageIdempotencyMarkersAsync(
            new IdempotencyMarker(Key(), LongAgo));

        (await fixture.PurgeRetentionAsync()).ShouldBe((Outbox: 1, Inbox: 1, Idempotency: 1));
    }
}
