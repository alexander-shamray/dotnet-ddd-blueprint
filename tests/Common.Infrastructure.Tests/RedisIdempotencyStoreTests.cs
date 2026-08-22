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

    [Fact]
    public async Task A_claimed_key_cannot_be_claimed_again()
    {
        // SET NX, and the whole contract of the port's first member: the race
        // has exactly one winner. This is the claim a ConcurrentDictionary
        // double cannot make on the real store's behalf.
        await using ServiceProvider provider = fixture.BuildProvider("idem");
        IIdempotencyStore store = provider.GetRequiredService<IIdempotencyStore>();

        bool first = await store.TryClaimAsync("contend", Retention, TestContext.Current.CancellationToken);
        bool second = await store.TryClaimAsync("contend", Retention, TestContext.Current.CancellationToken);

        first.ShouldBeTrue();
        second.ShouldBeFalse();
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

        await store.TryClaimAsync("done", Retention, TestContext.Current.CancellationToken);
        await store.CompleteAsync("done", "\"0195e4b2\"", Retention, TestContext.Current.CancellationToken);

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

        await store.TryClaimAsync("void", Retention, TestContext.Current.CancellationToken);
        await store.CompleteAsync("void", "null", Retention, TestContext.Current.CancellationToken);

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

        await store.TryClaimAsync("released", Retention, TestContext.Current.CancellationToken);
        await store.ReleaseAsync("released", TestContext.Current.CancellationToken);

        (await store.GetAsync("released", TestContext.Current.CancellationToken)).ShouldBeNull();
        (await store.TryClaimAsync("released", Retention, TestContext.Current.CancellationToken)).ShouldBeTrue();
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

        await store.TryClaimAsync("claimed", Retention, TestContext.Current.CancellationToken);
        (await database.KeyExistsAsync("prefixed:idem:claimed")).ShouldBeTrue();

        await store.CompleteAsync("claimed", "null", Retention, TestContext.Current.CancellationToken);
        (await database.StringGetAsync("prefixed:idem:claimed")).ToString().ShouldBe("null");
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

        await store.TryClaimAsync("expiring", Retention, TestContext.Current.CancellationToken);

        TimeSpan? claimTtl = await database.KeyTimeToLiveAsync("ttl:idem:expiring");
        claimTtl.ShouldNotBeNull();
        claimTtl.Value.ShouldBeLessThanOrEqualTo(Retention);

        // And CompleteAsync re-arms it rather than inheriting what the claim
        // had left: the claim's window measures how long an attempt may run,
        // this one how long the answer stays replayable.
        await store.CompleteAsync("expiring", "null", Retention, TestContext.Current.CancellationToken);

        TimeSpan? completedTtl = await database.KeyTimeToLiveAsync("ttl:idem:expiring");
        completedTtl.ShouldNotBeNull();
        completedTtl.Value.ShouldBeGreaterThan(TimeSpan.Zero);
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
            () => store.ReleaseAsync("never-held", TestContext.Current.CancellationToken));
    }
}
