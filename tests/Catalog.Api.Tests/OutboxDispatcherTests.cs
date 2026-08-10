using Catalog.TestSupport;
using Catalog.TestSupport.Outbox;
using Common.Infrastructure.Outbox;
using Shouldly;
using Xunit;

namespace Catalog.Api.Tests;

/// <summary>
/// §9.4's dispatcher, driven explicitly rather than by waiting on a timer.
/// These cover the behaviour §13.6 alerts on — per-row isolation and attempt
/// accounting — and neither is observable from a test that lets the background
/// service run, which is why <see cref="CatalogApiFactory"/> removes it.
/// </summary>
[Collection(nameof(IntegrationCollection))]
public sealed class OutboxDispatcherTests(ServiceFixture fixture) : IAsyncLifetime
{
    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task A_failing_row_does_not_block_healthy_rows()
    {
        await fixture.StageOutboxAsync(
            OutboxRows.Poison(fixture),          // its handler always throws
            OutboxRows.Healthy(fixture),
            OutboxRows.Healthy(fixture));

        (await fixture.ProcessOutboxBatchAsync()).ShouldBe(2);

        IReadOnlyList<OutboxMessage> rows = await fixture.OutboxAsync();

        rows.Count(r => r.ProcessedAt is not null).ShouldBe(2);

        OutboxMessage poison = rows.Single(r => r.ProcessedAt is null);
        poison.Attempts.ShouldBe(1);
        poison.LastError.ShouldNotBeNullOrEmpty();
        poison.LockedUntil.ShouldNotBeNull();     // backed off, not abandoned
    }

    [Fact]
    public async Task A_row_stops_being_claimed_at_the_attempt_cap()
    {
        OutboxMessage poison = OutboxRows.Poison(fixture);
        await fixture.StageOutboxAsync(poison);
        await fixture.SetOutboxAttemptsAsync(poison.MessageId, 9);

        (await fixture.ProcessOutboxBatchAsync()).ShouldBe(0);   // 9 → 10

        // Clear the backoff lease, so the second pass is blocked by the
        // attempt cap and nothing else. Without this the test would pass even
        // if the cap were removed entirely.
        await fixture.ExpireOutboxLeasesAsync();

        (await fixture.ProcessOutboxBatchAsync()).ShouldBe(0);

        OutboxMessage row = (await fixture.OutboxAsync()).ShouldHaveSingleItem();
        row.Attempts.ShouldBe(10);                // not 11 — never re-claimed
        row.ProcessedAt.ShouldBeNull();           // visible to the §13.6 alert
    }

    [Fact]
    public async Task A_local_row_with_no_registered_handler_fails_loudly()
    {
        // The one worth keeping forever. It asserts the failure mode that
        // would otherwise be invisible: a projection that never runs while
        // every dashboard stays green.
        await fixture.StageOutboxAsync(OutboxRows.Unhandled(fixture));

        await fixture.ProcessOutboxBatchAsync();

        OutboxMessage row = (await fixture.OutboxAsync()).ShouldHaveSingleItem();
        row.ProcessedAt.ShouldBeNull();           // NOT silently completed
        row.LastError.ShouldNotBeNull().ShouldContain("IProjectionHandler");
    }

    [Fact]
    public async Task A_claimed_row_is_leased_so_a_second_replica_skips_it()
    {
        // Two passes with no delivery possible in between: the second must
        // claim nothing, because the first pushed LockedUntil sixty seconds
        // out. This is the property READPAST and the lease exist for, and
        // without it two dispatcher replicas publish every message twice.
        await fixture.StageOutboxAsync(OutboxRows.Poison(fixture));

        (await fixture.ProcessOutboxBatchAsync()).ShouldBe(0);
        (await fixture.ProcessOutboxBatchAsync()).ShouldBe(0);

        OutboxMessage row = (await fixture.OutboxAsync()).ShouldHaveSingleItem();
        row.Attempts.ShouldBe(1, "the second pass must not have re-claimed a leased row");
    }

    [Fact]
    public async Task A_processed_row_is_never_claimed_again()
    {
        await fixture.StageOutboxAsync(OutboxRows.Healthy(fixture));

        (await fixture.ProcessOutboxBatchAsync()).ShouldBe(1);

        // At-least-once is the outbox's promise, but re-delivering a row the
        // dispatcher has already marked processed would be at-least-once
        // forever: nothing else in the design ever stops it.
        (await fixture.ProcessOutboxBatchAsync()).ShouldBe(0);
    }
}
