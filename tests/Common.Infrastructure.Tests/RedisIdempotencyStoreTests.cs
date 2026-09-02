using Common.Application;
using Common.Infrastructure.Redis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using StackExchange.Redis;
using Xunit;

namespace Common.Infrastructure.Tests;

/// <summary>
/// §8.5's store against a real server. The behaviour's own suite proves which
/// store call happens on which path; this proves the five calls mean what the
/// port says they mean — and three of the claims can only be made here, because
/// an in-memory double cannot disagree with itself about atomicity, a TTL, or
/// whether a key it was never told about is still held.
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

        string claim = await ClaimedAsync(store, "done", Retention);
        await store.CompleteAsync(
            "done", claim, "\"0195e4b2\"", TestContext.Current.CancellationToken);

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

        string claim = await ClaimedAsync(store, "void", Retention);
        await store.CompleteAsync("void", claim, "null", TestContext.Current.CancellationToken);

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

        string claim = await ClaimedAsync(store, "released", Retention);
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

        string claim = await ClaimedAsync(store, "claimed", Retention);
        (await database.KeyExistsAsync("prefixed:idem:claimed")).ShouldBeTrue();

        await store.CompleteAsync("claimed", claim, "null", TestContext.Current.CancellationToken);

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

        string claim = await ClaimedAsync(store, "expiring", Retention);

        TimeSpan? claimTtl = await database.KeyTimeToLiveAsync("ttl:idem:expiring");
        claimTtl.ShouldNotBeNull();
        claimTtl.Value.ShouldBeLessThanOrEqualTo(Retention);

    }

    [Fact]
    public async Task An_outcome_inherits_what_the_claim_had_left_rather_than_a_fresh_retention()
    {
        // #168, and the inversion of what this file asserted until it landed.
        // `CompleteAsync` re-armed the entry to a full retention, on the
        // reasoning that the claim's window measures how long an attempt may
        // run and this one how long the answer stays replayable. That started
        // the claim's window at the COMMIT — later than the marker §6.3 stamps
        // inside the transaction before it — so Redis outlived the marker by
        // the commit's tail, and the marker's own window had to be padded by
        // an allowance covering a lag nothing bounds. `KEEPTTL` removes the
        // term rather than bounding it.
        //
        // **The key is expired down first, and without that this test proves
        // nothing** — in either direction. A freshly claimed key still carries
        // almost the whole retention, so an assertion taken against it passes
        // whether the write inherited the expiry or replaced it with an
        // identical one. Shortening first is what makes the two outcomes
        // distinguishable, and it is why the assertion below is an upper bound
        // where the one it replaced was a lower one.
        await using ServiceProvider provider = fixture.BuildProvider("keepttl");
        IIdempotencyStore store = provider.GetRequiredService<IIdempotencyStore>();
        IDatabase database = provider
            .GetRequiredKeyedService<IConnectionMultiplexer>(RedisConnections.Coordination)
            .GetDatabase();

        string claim = await ClaimedAsync(store, "inherited", Retention);

        // Thirty seconds rather than five: the margin is against this test's
        // own runtime on a two-core runner, not against Redis. It has to stay
        // far below Retention for the assertion to mean anything, and far
        // above what a completion takes for the key to still be there.
        await database.KeyExpireAsync("keepttl:idem:inherited", TimeSpan.FromSeconds(30));

        await store.CompleteAsync("inherited", claim, "null", TestContext.Current.CancellationToken);

        TimeSpan? completedTtl = await database.KeyTimeToLiveAsync("keepttl:idem:inherited");

        completedTtl.ShouldNotBeNull("the outcome must still expire — KEEPTTL keeps a TTL, not none");
        completedTtl.Value.ShouldBeGreaterThan(TimeSpan.Zero);
        completedTtl.Value.ShouldBeLessThanOrEqualTo(
            TimeSpan.FromSeconds(30),
            "a re-armed entry would carry minutes; an inherited one carries what was left");

        // And the outcome is readable, so the assertion above is about the TTL
        // of a write that happened rather than of a claim nothing overwrote.
        IdempotencyEntry? entry = await store.GetAsync("inherited", TestContext.Current.CancellationToken);
        entry.ShouldNotBeNull();
        entry.InProgress.ShouldBeFalse();
        entry.Payload.ShouldBe("null");
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

        string stale = await ClaimedAsync(store, "outlived", Brief);
        string successor = await WaitForClaimAsync(store, "outlived");

        successor.ShouldNotBe(stale);

        await store.CompleteAsync(
            "outlived", stale, "\"clobbered\"", TestContext.Current.CancellationToken);

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

        string stale = await ClaimedAsync(store, "freed", Brief);
        await WaitForClaimAsync(store, "freed");

        await store.ReleaseAsync("freed", stale, TestContext.Current.CancellationToken);

        (await store.GetAsync("freed", TestContext.Current.CancellationToken))
            .ShouldNotBeNull("the successor still holds this key");
        (await store.TryClaimAsync("freed", Retention, TestContext.Current.CancellationToken))
            .ShouldBeNull("a freed key would let a third attempt in");
    }

    [Fact]
    public async Task A_pre_token_claim_reads_back_as_in_progress()
    {
        // A claim written by the release before #127 landed is still inside
        // its retention when this one starts serving, and it carries no
        // token. The marker is read exactly as the store read it before the
        // token existed, which is sound for the reason it always was: no
        // serialised payload can spell a value that is not valid JSON.
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

    [Theory]
    [InlineData("legacy-void", "null")]
    [InlineData("legacy-value", "\"0195e4b2-0000-7000-8000-0000000000ff\"")]
    public async Task A_pre_token_outcome_still_replays(string key, string payload)
    {
        // The other half of the same predicate, and the half that was
        // unasserted while the store reported EVERY untokened value as in
        // progress. Both of these are what the previous release's
        // CompleteAsync actually wrote — the void case and a captured
        // Result<Guid> — and both are 38 characters or fewer with no
        // separator at index 32, so a shape test alone cannot tell them from
        // an unfinished claim.
        //
        // Reading them as in progress answers 409 to a retry of work that
        // committed, for the rest of the retention, and then lets the command
        // run a second time once the key expires. A replay is not a commit,
        // which is what "both answers decline the duplicate" concealed.
        await using ServiceProvider provider = fixture.BuildProvider("legacy");
        IIdempotencyStore store = provider.GetRequiredService<IIdempotencyStore>();
        IDatabase database = provider
            .GetRequiredKeyedService<IConnectionMultiplexer>(RedisConnections.Coordination)
            .GetDatabase();

        await database.StringSetAsync($"legacy:idem:{key}", payload, Retention);

        IdempotencyEntry? entry = await store.GetAsync(key, TestContext.Current.CancellationToken);

        entry.ShouldNotBeNull();
        entry.InProgress.ShouldBeFalse("a completed pre-token entry is replayable, not in flight");
        entry.Payload.ShouldBe(payload);
    }

    [Fact]
    public async Task The_stores_scripts_run_under_the_documented_ACL_grant()
    {
        // §8.1 grants `+eval` and explains it by the LOCK's token-checked
        // release. Since #127 the store evaluates scripts too, so that
        // explanation now has a second consumer — and a premise about who
        // calls a thing is falsified by the next caller. The grant already
        // covers this; nothing proved it, which is the half that matters,
        // because EVAL is `@scripting` and none of the data categories
        // include it. Under the shorter grant this line used to print, every
        // complete and every release would throw.
        ConfigurationOptions admin = ConfigurationOptions.Parse(fixture.CoordinationConnectionString);
        admin.AllowAdmin = true;
        await using ConnectionMultiplexer adminConnection = await ConnectionMultiplexer.ConnectAsync(admin);
        object[] grant =
        [
            "SETUSER",
            "aclidem-svc",
            "reset",
            "on",
            ">s3cret",
            "~aclidem:*",
            "+@read",
            "+@write",
            "+@keyspace",
            "+@connection",
            "+eval",
            "-@dangerous",
            "+client|setname",
            "+client|setinfo"
        ];
        await adminConnection.GetServer(adminConnection.GetEndPoints()[0]).ExecuteAsync("ACL", grant);

        ConfigurationOptions restricted = ConfigurationOptions.Parse(fixture.CoordinationConnectionString);
        restricted.User = "aclidem-svc";
        restricted.Password = "s3cret";
        await using ConnectionMultiplexer connection = await ConnectionMultiplexer.ConnectAsync(restricted);

        ServiceCollection services = new();
        services.AddSingleton<IHostEnvironment>(new TestEnvironment("aclidem"));

        // The store logs a lost claim and a failed release, so this bare
        // collection needs a logger where the lock's equivalent test does not
        // — the lock has none to fail over.
        services.AddLogging();
        services.AddRedisConnections(AddRedisConnectionsTests.Configuration());
        services.AddKeyedSingleton<IConnectionMultiplexer>(RedisConnections.Coordination, connection);
        await using ServiceProvider provider = services.BuildServiceProvider();
        IIdempotencyStore store = provider.GetRequiredService<IIdempotencyStore>();

        // CompleteAsync fails LOUDLY without the grant — it does not catch —
        // so reading the payload back proves the script ran rather than that
        // nothing threw.
        string completed = await ClaimedAsync(store, "acl-done", Retention);
        await store.CompleteAsync(
            "acl-done", completed, "\"ok\"", TestContext.Current.CancellationToken);

        IdempotencyEntry? entry = await store.GetAsync("acl-done", TestContext.Current.CancellationToken);
        entry.ShouldNotBeNull();
        entry.Payload.ShouldBe("\"ok\"");

        // ReleaseAsync SWALLOWS a RedisException by design, so a missing grant
        // would leave the claim standing and log rather than throw. The
        // re-claim is what makes that visible — the lock suite's own argument,
        // and the reason this half cannot be asserted by "it did not throw".
        string released = await ClaimedAsync(store, "acl-freed", Retention);
        await store.ReleaseAsync("acl-freed", released, TestContext.Current.CancellationToken);

        (await store.TryClaimAsync("acl-freed", Retention, TestContext.Current.CancellationToken))
            .ShouldNotBeNull("a release that never ran would hold the key for its whole retention");
    }

    [Fact]
    public async Task A_key_this_store_never_saw_is_unheld()
    {
        // The purge's ordinary case, and the one the port names as its only
        // available answer: absent and expired are indistinguishable here, and
        // the caller only ever asks about keys that were claimed once.
        await using ServiceProvider provider = fixture.BuildProvider("idem");
        IIdempotencyStore store = provider.GetRequiredService<IIdempotencyStore>();

        IReadOnlyCollection<string> unheld =
            await store.UnheldAsync(["never-claimed"], TestContext.Current.CancellationToken);

        unheld.ShouldBe(["never-claimed"]);
    }

    [Fact]
    public async Task A_live_claim_is_held()
    {
        // ADR-039's whole point, stated at the store: while this answers "held",
        // §9.5's purge keeps the marker behind the key however old the row is,
        // and no clock is consulted by either side.
        await using ServiceProvider provider = fixture.BuildProvider("idem");
        IIdempotencyStore store = provider.GetRequiredService<IIdempotencyStore>();

        await ClaimedAsync(store, "unheld-live", Retention);

        IReadOnlyCollection<string> unheld =
            await store.UnheldAsync(["unheld-live"], TestContext.Current.CancellationToken);

        unheld.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_recorded_outcome_is_held_for_what_the_claim_had_left()
    {
        // A completed key is still an entry, and this is the state the purge
        // meets in practice: the command committed, CompleteAsync wrote the
        // payload under KEEPTTL, and the marker must outlive that remainder.
        // Reading only the in-progress state would purge every marker the
        // moment its command succeeded, which is the opposite of the guarantee.
        await using ServiceProvider provider = fixture.BuildProvider("idem");
        IIdempotencyStore store = provider.GetRequiredService<IIdempotencyStore>();

        string claim = await ClaimedAsync(store, "unheld-complete", Retention);
        await store.CompleteAsync("unheld-complete", claim, "42", TestContext.Current.CancellationToken);

        IReadOnlyCollection<string> unheld =
            await store.UnheldAsync(["unheld-complete"], TestContext.Current.CancellationToken);

        unheld.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_released_claim_is_unheld()
    {
        // The lost-acknowledgement path leaves exactly this state — the work
        // committed, the exception released the claim — and the marker is what
        // refuses the retry. Nothing here keeps the marker alive beyond its own
        // window once the key is free, which is correct: the window is what
        // bounds the guarantee, and this member only stops it ending early.
        await using ServiceProvider provider = fixture.BuildProvider("idem");
        IIdempotencyStore store = provider.GetRequiredService<IIdempotencyStore>();

        string claim = await ClaimedAsync(store, "unheld-released", Retention);
        await store.ReleaseAsync("unheld-released", claim, TestContext.Current.CancellationToken);

        IReadOnlyCollection<string> unheld =
            await store.UnheldAsync(["unheld-released"], TestContext.Current.CancellationToken);

        unheld.ShouldBe(["unheld-released"]);
    }

    [Fact]
    public async Task An_expired_claim_becomes_unheld_without_anybody_deleting_it()
    {
        // The property ADR-039 preserved and shape 1 of #171 would have cost:
        // a claim expires on its own, so the marker behind it becomes purgeable
        // without a second mechanism having to notice. Brief rather than the
        // shipped 24 hours for the reason that constant states.
        await using ServiceProvider provider = fixture.BuildProvider("idem");
        IIdempotencyStore store = provider.GetRequiredService<IIdempotencyStore>();

        await ClaimedAsync(store, "unheld-expiring", Brief);

        // Polled rather than slept, on WaitForClaimAsync's terms: a fixed wait
        // is either slower than it needs to be or short on a loaded runner.
        //
        // AND IT THROWS ON THE DEADLINE RATHER THAN ASSERTING PAST IT, which is
        // that helper's other half and the part the first draft of this test
        // dropped. Falling out of the loop left `unheld` holding the last
        // answer — empty — so a runner too loaded to expire a one-second key in
        // fifteen seconds failed with "expected [unheld-expiring], was []",
        // which reads as a broken store rather than as a slow machine. This
        // suite shares four containers with twelve other projects; the
        // difference decides whether the next red run is diagnosed or retried.
        DateTimeOffset deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);

        while (true)
        {
            IReadOnlyCollection<string> unheld =
                await store.UnheldAsync(["unheld-expiring"], TestContext.Current.CancellationToken);

            if (unheld.Count == 1)
            {
                unheld.ShouldBe(["unheld-expiring"], "the claim's own TTL is what frees the key");
                return;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException(
                    "'unheld-expiring' was still held 30 seconds after a one-second claim. The " +
                    "store answered, so this is the machine rather than the contract — a Redis " +
                    "container starved of CPU expires keys late.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task A_mixed_set_is_answered_key_by_key()
    {
        // The batch case, and the one a per-key loop could get wrong by
        // returning the right COUNT against the wrong keys — the answers are
        // zipped back by position, so a reordering inside the implementation
        // would swap held for unheld and delete the wrong markers.
        await using ServiceProvider provider = fixture.BuildProvider("idem");
        IIdempotencyStore store = provider.GetRequiredService<IIdempotencyStore>();

        await ClaimedAsync(store, "mixed-held-one", Retention);
        await ClaimedAsync(store, "mixed-held-two", Retention);

        IReadOnlyCollection<string> unheld = await store.UnheldAsync(
            ["mixed-gone-one", "mixed-held-one", "mixed-gone-two", "mixed-held-two"],
            TestContext.Current.CancellationToken);

        unheld.ShouldBe(["mixed-gone-one", "mixed-gone-two"], ignoreOrder: true);
    }

    [Fact]
    public async Task No_keys_is_no_question()
    {
        // An empty pass is the steady state once a service's markers are
        // drained, and it must cost nothing rather than a round trip per hour
        // per service.
        await using ServiceProvider provider = fixture.BuildProvider("idem");
        IIdempotencyStore store = provider.GetRequiredService<IIdempotencyStore>();

        IReadOnlyCollection<string> unheld =
            await store.UnheldAsync([], TestContext.Current.CancellationToken);

        unheld.ShouldBeEmpty();
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

    /// <summary>
    /// A claim every caller below needs to have succeeded, with the failure
    /// reported as the precondition it is.
    /// </summary>
    /// <remarks>
    /// <b>This replaced a null-forgiving <c>!</c> at nine call sites.</b> A
    /// refused claim written that way is a <c>null</c> travelling several lines
    /// before something dereferences it, so what CI prints names the line that
    /// tripped over the value rather than the line that produced it — and on a
    /// suite whose failures are usually about the container rather than the
    /// branch, the diagnosis is most of the cost. Failing at the claim says
    /// which key, and says that the precondition is what went wrong.
    /// </remarks>
    private static async Task<string> ClaimedAsync(IIdempotencyStore store, string key, TimeSpan retention)
    {
        string? claim = await store.TryClaimAsync(key, retention, TestContext.Current.CancellationToken);

        if (claim is null)
        {
            throw new InvalidOperationException(
                $"'{key}' could not be claimed, so this test never reached its subject. " +
                $"The key was already held — a leftover from an earlier run against a reused server.");
        }

        return claim;
    }
}
