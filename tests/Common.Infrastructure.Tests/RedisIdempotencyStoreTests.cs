using Common.Application;
using Common.Infrastructure.Redis;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using StackExchange.Redis;
using Xunit;

namespace Common.Infrastructure.Tests;

/// <summary>
/// §8.5's store against a real server. The behaviour's own suite proves which
/// store call happens on which path; this proves the four calls mean what the
/// port says they mean — and two of the claims can only be made here, because
/// an in-memory double cannot disagree with itself about atomicity or a TTL.
/// </summary>
/// <remarks>
/// Each test takes its own key, so the shared container never couples them.
/// </remarks>
[Collection(nameof(IntegrationCollection))]
public sealed class RedisIdempotencyStoreTests(RedisFixture fixture)
{
    private static readonly TimeSpan Retention = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Short enough that a claim taken under it lapses inside a test, which is
    /// the only way to reach #127's case: the shipped caller passes 24 hours,
    /// so the window in which two attempts hold one key in turn cannot be
    /// observed at the setting that ships. Every successor then claims under
    /// <see cref="Retention"/>, so it outlives the assertions about it.
    /// </summary>
    private static readonly TimeSpan Brief = TimeSpan.FromSeconds(1);

    /// <summary>
    /// A token no claim ever minted, for the paths where the caller holds
    /// none. Not a well-formed one on purpose: nothing about the scripts
    /// depends on the shape, and a real-looking token here would read as a
    /// claim this test had taken.
    /// </summary>
    private const string Foreign = "not-a-claim-this-test-holds";

    [Fact]
    public async Task A_claimed_key_cannot_be_claimed_again()
    {
        // SET NX, and the whole contract of the port's first member: the race
        // has exactly one winner. This is the claim a ConcurrentDictionary
        // double cannot make on the real store's behalf.
        await using ServiceProvider provider = fixture.BuildProvider("idem");
        IIdempotencyStore store = provider.GetRequiredService<IIdempotencyStore>();

        string? first = await store.TryClaimAsync("contend", Retention, TestContext.Current.CancellationToken);
        string? second = await store.TryClaimAsync("contend", Retention, TestContext.Current.CancellationToken);

        first.ShouldNotBeNull();
        second.ShouldBeNull();
    }

    [Fact]
    public async Task A_fresh_claim_reads_back_as_in_progress_and_carries_no_payload()
    {
        await using ServiceProvider provider = fixture.BuildProvider("idem");
        IIdempotencyStore store = provider.GetRequiredService<IIdempotencyStore>();

        await store.TryClaimAsync("in-flight", Retention, TestContext.Current.CancellationToken);
        IdempotencyEntry? entry = await store.GetAsync("in-flight", TestContext.Current.CancellationToken);

        entry.ShouldNotBeNull();
        entry.InProgress.ShouldBeTrue();
        entry.Payload.ShouldBeNull();
    }

    [Fact]
    public async Task A_completed_key_reads_back_the_payload_and_is_no_longer_in_progress()
    {
        await using ServiceProvider provider = fixture.BuildProvider("idem");
        IIdempotencyStore store = provider.GetRequiredService<IIdempotencyStore>();

        string claim = (await store.TryClaimAsync("done", Retention, TestContext.Current.CancellationToken))!;
        await store.CompleteAsync(
            "done", claim, "\"0195e4b2\"", Retention, TestContext.Current.CancellationToken);

        IdempotencyEntry? entry = await store.GetAsync("done", TestContext.Current.CancellationToken);

        entry.ShouldNotBeNull();
        entry.InProgress.ShouldBeFalse();
        entry.Payload.ShouldBe("\"0195e4b2\"");
    }

    [Fact]
    public async Task The_void_payload_is_told_apart_from_the_in_progress_marker()
    {
        // "null" is a real recorded outcome and must not read as absent. The
        // marker is deliberately not valid JSON so that no serialised value
        // can spell it — this is the test that would fail if someone
        // "tidied" the marker into the empty string, which would replay every
        // void command as a refusal for the whole retention.
        await using ServiceProvider provider = fixture.BuildProvider("idem");
        IIdempotencyStore store = provider.GetRequiredService<IIdempotencyStore>();

        string claim = (await store.TryClaimAsync("void", Retention, TestContext.Current.CancellationToken))!;
        await store.CompleteAsync("void", claim, "null", Retention, TestContext.Current.CancellationToken);

        IdempotencyEntry? entry = await store.GetAsync("void", TestContext.Current.CancellationToken);

        entry.ShouldNotBeNull();
        entry.InProgress.ShouldBeFalse("a stored \"null\" is an outcome, not a missing one");
        entry.Payload.ShouldBe("null");
    }

    [Fact]
    public async Task A_released_key_is_claimable_again()
    {
        await using ServiceProvider provider = fixture.BuildProvider("idem");
        IIdempotencyStore store = provider.GetRequiredService<IIdempotencyStore>();

        string claim =
            (await store.TryClaimAsync("released", Retention, TestContext.Current.CancellationToken))!;
        await store.ReleaseAsync("released", claim, TestContext.Current.CancellationToken);

        (await store.GetAsync("released", TestContext.Current.CancellationToken)).ShouldBeNull();
        (await store.TryClaimAsync("released", Retention, TestContext.Current.CancellationToken))
            .ShouldNotBeNull();
    }

    [Fact]
    public async Task An_unknown_key_reads_back_as_nothing()
    {
        await using ServiceProvider provider = fixture.BuildProvider("idem");
        IIdempotencyStore store = provider.GetRequiredService<IIdempotencyStore>();

        (await store.GetAsync("never-claimed", TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task Both_writes_carry_the_service_prefixed_idem_namespace()
    {
        // §8.1's ACL is a key pattern, so a key written outside {service}:idem:
        // is refused by the server in an environment no test here runs — the
        // failure would appear first in production. Asserted against the raw
        // database rather than through the port, because the port is exactly
        // what hides the prefix.
        await using ServiceProvider provider = fixture.BuildProvider("prefixed");
        IIdempotencyStore store = provider.GetRequiredService<IIdempotencyStore>();
        IDatabase database = provider
            .GetRequiredKeyedService<IConnectionMultiplexer>(RedisConnections.Coordination)
            .GetDatabase();

        string claim =
            (await store.TryClaimAsync("claimed", Retention, TestContext.Current.CancellationToken))!;
        (await database.KeyExistsAsync("prefixed:idem:claimed")).ShouldBeTrue();

        await store.CompleteAsync("claimed", claim, "null", Retention, TestContext.Current.CancellationToken);

        // The stored value carries the claim token AND the payload, which is
        // the encoding the two scripts compare on. Asserted here rather than
        // through the port, because the port is what hides it — and a change
        // to the separator or the token width is a change to what a running
        // release can still read.
        (await database.StringGetAsync("prefixed:idem:claimed")).ToString().ShouldBe($"{claim}:null");
    }

    [Fact]
    public async Task The_claim_is_written_to_the_coordination_server_and_not_the_cache_one()
    {
        // §8.1's split, and the reason the fixture runs two servers. An
        // idempotency key on the allkeys-lru instance is evicted under exactly
        // the memory pressure that makes the duplicate write hardest to
        // reproduce — and with one server playing both roles, this test would
        // pass while the wiring was wrong.
        await using ServiceProvider provider = fixture.BuildProvider("routed");
        IIdempotencyStore store = provider.GetRequiredService<IIdempotencyStore>();

        await store.TryClaimAsync("routing", Retention, TestContext.Current.CancellationToken);

        IDatabase cache = provider
            .GetRequiredKeyedService<IConnectionMultiplexer>(RedisConnections.Cache)
            .GetDatabase();

        (await cache.KeyExistsAsync("routed:idem:routing")).ShouldBeFalse();
    }

    [Fact]
    public async Task A_claim_carries_the_retention_as_a_time_to_live()
    {
        // The guarantee §8.5 opens with is bounded in time, and this is the
        // half of it the store owns. A claim written without a TTL would hold
        // a key for ever after a crash between the claim and the outcome —
        // turning a bounded exposure into a command nobody can ever retry.
        await using ServiceProvider provider = fixture.BuildProvider("ttl");
        IIdempotencyStore store = provider.GetRequiredService<IIdempotencyStore>();
        IDatabase database = provider
            .GetRequiredKeyedService<IConnectionMultiplexer>(RedisConnections.Coordination)
            .GetDatabase();

        string claim =
            (await store.TryClaimAsync("expiring", Retention, TestContext.Current.CancellationToken))!;

        TimeSpan? claimTtl = await database.KeyTimeToLiveAsync("ttl:idem:expiring");
        claimTtl.ShouldNotBeNull();
        claimTtl.Value.ShouldBeLessThanOrEqualTo(Retention);

        // And CompleteAsync RE-ARMS it rather than inheriting what the claim
        // had left: the claim's window measures how long an attempt may run,
        // this one how long the answer stays replayable.
        //
        // **The key is expired down first, and without that this test proves
        // nothing.** It used to assert only that the completed TTL was above
        // zero — which a freshly claimed key satisfies with almost the whole
        // retention still on it, so the assertion passed just as well if
        // CompleteAsync had preserved the claim's expiry. An assertion that
        // cannot fail is worse than none, because it is the line a reader
        // trusts instead of checking.
        await database.KeyExpireAsync("ttl:idem:expiring", TimeSpan.FromSeconds(5));

        TimeSpan? shortened = await database.KeyTimeToLiveAsync("ttl:idem:expiring");
        shortened!.Value.ShouldBeLessThan(TimeSpan.FromSeconds(10));

        await store.CompleteAsync(
            "expiring", claim, "null", Retention, TestContext.Current.CancellationToken);

        TimeSpan? completedTtl = await database.KeyTimeToLiveAsync("ttl:idem:expiring");
        completedTtl.ShouldNotBeNull();

        // Back near the full retention rather than merely non-zero. The
        // bound is loose on the low side only — anything above the five
        // seconds just set proves the write re-armed rather than inherited.
        completedTtl.Value.ShouldBeGreaterThan(TimeSpan.FromMinutes(1));
        completedTtl.Value.ShouldBeLessThanOrEqualTo(Retention);
    }

    [Fact]
    public async Task Releasing_a_key_that_is_not_held_is_not_an_error()
    {
        // The behaviour releases from a catch block before rethrowing, and on
        // one path the key may already be gone. A throw here would destroy the
        // fault the caller was reporting rather than wrapping it (§8.5).
        await using ServiceProvider provider = fixture.BuildProvider("idem");
        IIdempotencyStore store = provider.GetRequiredService<IIdempotencyStore>();

        await Should.NotThrowAsync(
            () => store.ReleaseAsync("never-held", Foreign, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_claim_that_outlived_its_retention_cannot_complete_over_its_successors()
    {
        // #127, and the reason it needs a SHORT retention: the shipped caller
        // passes 24 hours, so the window in which two attempts hold one key in
        // turn is real and unobservable at that setting. Nothing in the port's
        // contract says a retention must outlast a handler, and the first
        // caller that passes seconds gets this race with no diagnostic.
        await using ServiceProvider provider = fixture.BuildProvider("stale");
        IIdempotencyStore store = provider.GetRequiredService<IIdempotencyStore>();

        string stale =
            (await store.TryClaimAsync("outlived", Brief, TestContext.Current.CancellationToken))!;
        string successor = await WaitForClaimAsync(store, "outlived");

        successor.ShouldNotBe(stale);

        await store.CompleteAsync(
            "outlived", stale, "\"clobbered\"", Retention, TestContext.Current.CancellationToken);

        IdempotencyEntry? entry = await store.GetAsync("outlived", TestContext.Current.CancellationToken);

        entry.ShouldNotBeNull();
        entry.InProgress.ShouldBeTrue("the successor's claim is still in flight");
        entry.Payload.ShouldBeNull("a lost claim must not record an outcome over a live one");
    }

    [Fact]
    public async Task A_claim_that_outlived_its_retention_cannot_release_its_successors()
    {
        // The worse half of #127. An overwrite corrupts the record of one
        // duplicate; a delete FREES a successor's claim while that successor
        // is still running, which admits a concurrent duplicate — the thing
        // the whole section exists to refuse.
        await using ServiceProvider provider = fixture.BuildProvider("stale");
        IIdempotencyStore store = provider.GetRequiredService<IIdempotencyStore>();

        string stale =
            (await store.TryClaimAsync("freed", Brief, TestContext.Current.CancellationToken))!;
        await WaitForClaimAsync(store, "freed");

        await store.ReleaseAsync("freed", stale, TestContext.Current.CancellationToken);

        (await store.GetAsync("freed", TestContext.Current.CancellationToken))
            .ShouldNotBeNull("the successor still holds this key");
        (await store.TryClaimAsync("freed", Retention, TestContext.Current.CancellationToken))
            .ShouldBeNull("a freed key would let a third attempt in");
    }

    [Fact]
    public async Task An_entry_written_before_the_token_reads_back_as_in_progress()
    {
        // A claim written by the release before #127 landed is still inside
        // its retention when this one starts serving, and it carries no token.
        // The store declines to guess: reporting it in progress refuses the
        // caller until it expires, where reading it as a payload would replay
        // something nothing can attribute. Both answers decline the duplicate
        // commit; only this one declines to invent an owner.
        await using ServiceProvider provider = fixture.BuildProvider("legacy");
        IIdempotencyStore store = provider.GetRequiredService<IIdempotencyStore>();
        IDatabase database = provider
            .GetRequiredKeyedService<IConnectionMultiplexer>(RedisConnections.Coordination)
            .GetDatabase();

        await database.StringSetAsync("legacy:idem:untokened", "in-progress", Retention);

        IdempotencyEntry? entry = await store.GetAsync("untokened", TestContext.Current.CancellationToken);

        entry.ShouldNotBeNull();
        entry.InProgress.ShouldBeTrue();
        entry.Payload.ShouldBeNull();
    }

    /// <summary>
    /// Claims <paramref name="key"/> as soon as the previous claim's retention
    /// lapses, under the long retention so the successor survives the
    /// assertions that follow.
    /// </summary>
    /// <remarks>
    /// Polling rather than a fixed sleep, on <c>DistributedLockRedisTests</c>'
    /// terms: a fixed wait is either slower than it needs to be or
    /// intermittently short on a loaded runner, and this suite already has one
    /// helper written for that reason.
    /// </remarks>
    private static async Task<string> WaitForClaimAsync(IIdempotencyStore store, string key)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(15);

        while (DateTimeOffset.UtcNow < deadline)
        {
            string? claim = await store.TryClaimAsync(key, Retention, TestContext.Current.CancellationToken);

            if (claim is not null)
                return claim;

            await Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
        }

        throw new TimeoutException($"The claim on '{key}' had not expired after 15 seconds.");
    }
}
