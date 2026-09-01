using System.Diagnostics.Metrics;
using Ordering.Infrastructure.Persistence;
using Ordering.TestSupport;
using Common.Infrastructure.Inbox;
using Common.Infrastructure.Messaging;
using MassTransit;
using MassTransit.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Ordering.Api.Tests;

/// <summary>
/// §9.5's inbox filter, over a real consume pipeline and the real table. The
/// in-memory transport rather than RabbitMQ, because what is under test is the
/// filter's own arithmetic — which of <c>MessageId</c> and <c>Endpoint</c> the
/// row is keyed on, and when it is committed — and both are properties of the
/// consume context rather than of the broker.
/// </summary>
/// <remarks>
/// This suite declares its own endpoints and its own probe message, and keeps
/// doing so now that <c>ordering-catalog-events</c> exists (PR-20). Two
/// endpoints are what make the composite key a claim, one consumer has to
/// throw and another has to clear the change tracker — none of which the real
/// endpoint's consumers do, and arranging them onto it would be testing the
/// projection rather than the filter.
/// </remarks>
[Collection(nameof(IntegrationCollection))]
public sealed class InboxFilterTests(ServiceFixture fixture) : IAsyncLifetime
{
    private const string FirstEndpoint = "probe-events";
    private const string SecondEndpoint = "probe-events-bulk";

    public sealed record ProbeMessage(Guid Id);

    /// <summary>Counts every delivery that reached past the filter.</summary>
    public sealed class FirstConsumer : IConsumer<ProbeMessage>
    {
        public static readonly List<Guid> Consumed = [];

        public Task Consume(ConsumeContext<ProbeMessage> context)
        {
            lock (Consumed)
                Consumed.Add(context.Message.Id);

            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// A second consumer on a second endpoint, which is what makes the
    /// composite key a claim: the same message must be processed once per
    /// endpoint, and a key of <c>MessageId</c> alone would let whichever
    /// finished first suppress the other.
    /// </summary>
    public sealed class SecondConsumer : IConsumer<ProbeMessage>
    {
        public static readonly List<Guid> Consumed = [];

        public Task Consume(ConsumeContext<ProbeMessage> context)
        {
            lock (Consumed)
                Consumed.Add(context.Message.Id);

            return Task.CompletedTask;
        }
    }

    /// <summary>Fails every delivery, so the ordering inside the filter is observable.</summary>
    public sealed class ThrowingConsumer : IConsumer<ProbeMessage>
    {
        public Task Consume(ConsumeContext<ProbeMessage> context) =>
            throw new InvalidOperationException("this consumer always throws");
    }

    /// <summary>
    /// Clears the change tracker on the service's context, which is the first
    /// thing <c>EfUnitOfWork.ExecuteAsync</c> does on every attempt (§7.5,
    /// PR-09) — and therefore the first thing every message-borne command does,
    /// since §6.3's <c>TransactionBehavior</c> wraps each handler in that call.
    /// </summary>
    /// <remarks>
    /// <b>The line rather than the type, and the reason is access rather than
    /// preference.</b> <c>EfUnitOfWork</c> is internal to
    /// <c>Ordering.Infrastructure</c> — nothing outside resolves it by type,
    /// only through <c>IUnitOfWork</c>, and registering it here would need an
    /// <c>InternalsVisibleTo</c> for one call. What has to be reproduced is the
    /// interaction, not the wrapper: a tracked entity added before the consumer
    /// runs does not survive a consumer that clears the tracker, and this is
    /// the shortest consumer that does that on the same context the filter
    /// writes through.
    /// </remarks>
    public sealed class ClearsTheChangeTrackerConsumer(DbContext db) : IConsumer<ProbeMessage>
    {
        public Task Consume(ConsumeContext<ProbeMessage> context)
        {
            db.ChangeTracker.Clear();

            return Task.CompletedTask;
        }
    }

    public async ValueTask InitializeAsync()
    {
        FirstConsumer.Consumed.Clear();
        SecondConsumer.Consumed.Clear();

        await fixture.ResetAsync();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>
    /// A host with the real context and one or two filtered endpoints. The
    /// alias registration is the production one verbatim — a test that
    /// registered <c>AddScoped&lt;DbContext, OrderingDbContext&gt;()</c> here
    /// would pass every assertion below while proving nothing about the
    /// transaction the filter is supposed to share.
    /// </summary>
    private ServiceProvider BuildHost<TConsumer>(bool withSecondEndpoint = false)
        where TConsumer : class, IConsumer<ProbeMessage>
    {
        ServiceCollection services = new();

        services.AddDbContext<OrderingDbContext>(o => o.UseSqlServer(fixture.ConnectionString));
        services.AddScoped<DbContext>(sp => sp.GetRequiredService<OrderingDbContext>());

        // The clock the filter stamps HandledAt from. The retention purge reads
        // its cutoff from the same abstraction, which is the point: §12.7 makes
        // the clock a seam, and two clocks for one window is a row that looks
        // expired the moment it is written.
        services.AddSingleton(TimeProvider.System);

        // The filter's two observability dependencies (#64). A suppressed
        // message is counted and logged rather than dropped in silence, so
        // this host supplies the meter factory and the logger a real one does
        // — and `validateScopes: true` below means a missing registration is a
        // resolution failure at the first delivery rather than a wrong answer.
        services.AddMetrics();
        services.AddLogging();
        services.AddSingleton<MessagingMetrics>();

        services.AddMassTransitTestHarness(x =>
        {
            x.SetTestTimeouts(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(30));
            x.AddConsumer<TConsumer>();

            if (withSecondEndpoint)
                x.AddConsumer<SecondConsumer>();

            x.UsingInMemory((context, cfg) =>
            {
                cfg.ReceiveEndpoint(FirstEndpoint, e =>
                {
                    e.UseConsumeFilter(typeof(InboxFilter<>), context);
                    e.ConfigureConsumer<TConsumer>(context);
                });

                if (!withSecondEndpoint)
                    return;

                cfg.ReceiveEndpoint(SecondEndpoint, e =>
                {
                    e.UseConsumeFilter(typeof(InboxFilter<>), context);
                    e.ConfigureConsumer<SecondConsumer>(context);
                });
            });
        });

        return services.BuildServiceProvider(validateScopes: true);
    }

    [Fact]
    public async Task A_redelivered_message_is_dropped_and_its_consumer_runs_once()
    {
        await using ServiceProvider provider = BuildHost<FirstConsumer>();
        ITestHarness harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var id = Guid.CreateVersion7();
        var messageId = Guid.CreateVersion7();

        // The same transport id twice, which is what a redelivery is. §9.1's
        // single-identity rule is what makes this the id the inbox keys on:
        // body, row, header and inbox key are one GUID.
        //
        // Sequenced, not published back to back, and the sequencing is the
        // claim rather than a convenience. A redelivery follows a failure or a
        // broker retry — it arrives *after* the first attempt finished, which
        // is the only case the filter suppresses. Two deliveries genuinely in
        // flight at once both pass the AnyAsync check before either row is
        // committed, so both run; the composite primary key then fails the
        // second SaveChanges and the message is retried into the suppression
        // this test is about. That is §9.5's own "duplicate suppression, not
        // an atomic guarantee — the common duplicate, not every duplicate",
        // and asserting otherwise here would be asserting something the
        // chapter does not claim.
        await harness.Bus.Publish(
            new ProbeMessage(id),
            c => c.MessageId = messageId,
            TestContext.Current.CancellationToken);

        // The row, not the consume: the row is what the second delivery reads,
        // and it is committed after the consumer returns. Scoped to this
        // message's own id — an unscoped wait returns on a row some other
        // class left behind, so the second publish would go out before this
        // one's row was committed and the filter would have nothing to read.
        await Eventually(() => fixture.InboxAsync(messageId), expected: 1);

        await harness.Bus.Publish(
            new ProbeMessage(id),
            c => c.MessageId = messageId,
            TestContext.Current.CancellationToken);

        // Waiting for BOTH deliveries to be recorded, not for "a" delivery.
        // `Consumed.Any<ProbeMessage>()` matches the first one the moment it
        // lands, so a wait on it after the second publish returns immediately
        // and the assertions below run while the redelivery is still in the
        // pipe — the test would then pass whether the filter suppressed the
        // duplicate or simply had not seen it yet, which is the wrong way
        // round for a duplicate-suppression test to fail.
        //
        // The filter runs ahead of the consumer, so a suppressed message is
        // consumed-and-dropped rather than never consumed: both deliveries
        // reach `Consumed` and only one reaches FirstConsumer, which is what
        // makes counting them the right signal.
        await Eventually(
            () => Task.FromResult<IReadOnlyList<object>>(
                [.. harness.Consumed.Select<ProbeMessage>()]),
            expected: 2);

        FirstConsumer.Consumed.ShouldBe([id], "the filter must drop the second delivery");

        // Scoped to this message (#166). The unscoped read made this line two
        // claims at once — that the duplicate wrote no second row, and that no
        // other row exists anywhere in the schema — and only the first is the
        // filter's. The second is test isolation, and it is the one that
        // failed: this collection runs its classes in sequence over one
        // fixture, so a message an earlier class published and a consumer
        // handled after this class's ResetAsync is a row this assertion had no
        // business counting. The endpoint is what is left to assert once the
        // id is in the query rather than in the assertion.
        (await fixture.InboxAsync(messageId))
            .ShouldHaveSingleItem("one delivery of this message reached the consumer, so one row")
            .Endpoint.ShouldBe(FirstEndpoint);
    }

    [Fact]
    public async Task A_suppressed_duplicate_is_counted_rather_than_dropped_in_silence()
    {
        // #64. The suppression branch was a bare `return;`, which made the one
        // path where this platform drops a message on purpose the only path
        // with no signal anywhere in §13 — so an inbox hit suppressing a
        // message the service had never seen read exactly like a genuine
        // redelivery, from every dashboard.
        //
        // Homed in this suite alone rather than in both services' copies:
        // InboxFilter<T> is common code and this is a property of the filter,
        // not of either service's wiring. The endpoint tag is what keeps the
        // listener from reading a neighbouring suite's suppressions — a
        // MeterListener is process-wide, where a collection is not.
        await using ServiceProvider provider = BuildHost<FirstConsumer>();
        ITestHarness harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        List<string> observed = [];
        using MeterListener listener = new();

        listener.InstrumentPublished = (instrument, active) =>
        {
            if (instrument.Meter.Name == "Commerce.Messaging" &&
                instrument.Name == "messaging.inbox.suppressed")
            {
                active.EnableMeasurementEvents(instrument);
            }
        };

        listener.SetMeasurementEventCallback<long>((_, measurement, tags, _) =>
        {
            string message = TagValue(tags, "message");
            string endpoint = TagValue(tags, "endpoint");

            if (endpoint != FirstEndpoint)
                return;

            lock (observed)
                observed.Add($"{measurement} {message} {endpoint}");
        });

        listener.Start();

        var id = Guid.CreateVersion7();
        var messageId = Guid.CreateVersion7();

        await harness.Bus.Publish(
            new ProbeMessage(id),
            c => c.MessageId = messageId,
            TestContext.Current.CancellationToken);

        // Scoped for the reason the test above gives at length: an unscoped
        // wait can be satisfied by a row this test did not write, and the
        // second publish would then race the first delivery's commit — the
        // suppression this test counts would simply not happen.
        await Eventually(() => fixture.InboxAsync(messageId), expected: 1);

        await harness.Bus.Publish(
            new ProbeMessage(id),
            c => c.MessageId = messageId,
            TestContext.Current.CancellationToken);

        // Both deliveries recorded before the assertion, for the reason the
        // test above spells out at length: waiting on "a" delivery returns
        // while the redelivery is still in the pipe, and the count would then
        // be read before the drop had happened.
        await Eventually(
            () => Task.FromResult<IReadOnlyList<object>>(
                [.. harness.Consumed.Select<ProbeMessage>()]),
            expected: 2);

        lock (observed)
            observed.ShouldBe([$"1 {nameof(ProbeMessage)} {FirstEndpoint}"]);
    }

    /// <summary>
    /// One tag off a measurement. A span cannot be captured, so the read
    /// happens inside the callback and only the string escapes.
    /// </summary>
    private static string TagValue(ReadOnlySpan<KeyValuePair<string, object?>> tags, string name)
    {
        foreach (KeyValuePair<string, object?> tag in tags)
        {
            if (tag.Key == name)
                return tag.Value?.ToString() ?? string.Empty;
        }

        return string.Empty;
    }

    [Fact]
    public async Task The_same_message_is_handled_once_per_endpoint()
    {
        // The composite key of §9.5. One service can legitimately bind the same
        // type on a normal queue and a bulk one, and each is a different unit
        // of work — keying on MessageId alone would silently drop the second.
        await using ServiceProvider provider = BuildHost<FirstConsumer>(withSecondEndpoint: true);
        ITestHarness harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var id = Guid.CreateVersion7();
        var messageId = Guid.CreateVersion7();

        await harness.Bus.Publish(
            new ProbeMessage(id),
            c => c.MessageId = messageId,
            TestContext.Current.CancellationToken);

        (await harness.Consumed.Any<ProbeMessage>(TestContext.Current.CancellationToken)).ShouldBeTrue();

        // Both endpoints, one message: two rows sharing a MessageId and
        // differing only in Endpoint, which is the shape the key exists for.
        // Scoping by message id still expects two — that is the point of the
        // key, and it is what makes this the one call site where the count
        // survives the change unaltered. Unscoped, the same wait would return
        // on one row of this message and one of somebody else's.
        IReadOnlyList<InboxMessage> rows =
            await Eventually(() => fixture.InboxAsync(messageId), expected: 2);

        // The endpoints are the whole claim now. `ShouldAllBe(r => r.MessageId
        // == messageId)` stood here and cannot fail against a read that
        // filters on exactly that — an assertion no arrangement can break is
        // this repository's named failure, so the claim moved into the query
        // rather than being restated after it.
        rows.Select(r => r.Endpoint).OrderBy(e => e).ShouldBe([FirstEndpoint, SecondEndpoint]);
    }

    [Fact]
    public async Task No_row_is_written_when_the_consumer_throws()
    {
        // The ordering inside the filter, and the one thing in it that must not
        // be rearranged: the consumer runs FIRST and the row is committed only
        // if it succeeded. Recording before would mark a message handled that
        // never was — and a suppressed redelivery is not retried, it is
        // dropped, so the loss is permanent.
        await using ServiceProvider provider = BuildHost<ThrowingConsumer>();
        ITestHarness harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        // Named rather than inlined, because the assertion below is now about
        // this message and needs to be able to say which one.
        var messageId = Guid.CreateVersion7();

        await harness.Bus.Publish(
            new ProbeMessage(Guid.CreateVersion7()),
            c => c.MessageId = messageId,
            TestContext.Current.CancellationToken);

        // The completed record, not `Consumed.Any`. That predicate is satisfied
        // when the harness observes the consume *attempt*, which can be before
        // the throwing pipeline has unwound — so the negative assertion below
        // could run while the attempt was still in flight and pass over a row
        // written a moment later. A negative assertion that can be satisfied by
        // "not yet" is the fail-open shape this suite has already been caught
        // by once, in the redelivery test.
        //
        // Waiting on the exception is what makes it the finished attempt: the
        // filter's SaveChangesAsync is downstream of `next.Send` throwing, so
        // by the time the fault is recorded there is nothing left to write.
        IReceivedMessage<ProbeMessage> received = await harness.Consumed
            .SelectAsync<ProbeMessage>(TestContext.Current.CancellationToken)
            .FirstOrDefault();

        received.ShouldNotBeNull();
        received.Exception.ShouldBeOfType<InvalidOperationException>();

        // "This message wrote no row", not "the table is empty". The second is
        // a claim about every other class in the collection, and a stray row
        // from one of them would fail a test about a consumer that throws —
        // which is the wrong test to fail and tells the reader nothing. The
        // scoped read still fails the moment the filter commits a row for a
        // consumer that faulted, which is the whole subject here.
        (await fixture.InboxAsync(messageId)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_consumer_that_clears_the_change_tracker_still_gets_its_inbox_row()
    {
        // The regression test for the defect this suite did not have. Every
        // message-borne command reaches §6.3's TransactionBehavior, which runs
        // the handler inside EfUnitOfWork.ExecuteAsync — and that opens each
        // attempt with db.ChangeTracker.Clear(), so PR-09's retry can never
        // re-commit the previous attempt's mutations.
        //
        // With the inbox row staged BEFORE next.Send, that clear discarded it:
        // SaveChangesAsync then wrote nothing, no command was ever recorded,
        // and every redelivery of every command was reprocessed. Nothing threw
        // and nothing logged — the table simply stayed empty. Two mechanisms
        // already in this blueprint, in tension, and invisible until a consumer
        // exercised both.
        //
        // The other suites here could not see it: their consumers do no work.
        await using ServiceProvider provider = BuildHost<ClearsTheChangeTrackerConsumer>();
        ITestHarness harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var messageId = Guid.CreateVersion7();

        await harness.Bus.Publish(
            new ProbeMessage(Guid.CreateVersion7()),
            c => c.MessageId = messageId,
            TestContext.Current.CancellationToken);

        (await harness.Consumed.Any<ProbeMessage>(TestContext.Current.CancellationToken)).ShouldBeTrue();

        IReadOnlyList<InboxMessage> rows =
            await Eventually(() => fixture.InboxAsync(messageId), expected: 1);

        // Scoped, so the wait cannot be satisfied by a row this consumer did
        // not write — which for a test whose defect was "the table simply
        // stayed empty" is the difference between the regression test and a
        // test of the collection's tidiness. The endpoint is what the
        // assertion has left to say, the id having moved into the query.
        rows.ShouldHaveSingleItem(
            "the row is staged after the consumer returns precisely so the unit of work's " +
            "ChangeTracker.Clear() cannot take it").Endpoint.ShouldBe(FirstEndpoint);
    }

    [Fact]
    public async Task The_filters_context_is_the_services_own_instance()
    {
        // AddScoped<DbContext, OrderingDbContext>() compiles, resolves and is
        // wrong: it builds a SECOND context in the same scope, so the inbox row
        // commits in its own transaction and §9.5's atomic row silently becomes
        // its non-atomic one. Nothing fails — the guarantee just stops holding,
        // which is why it is asserted rather than reviewed.
        //
        // Read from the real host, not from this suite's, so the assertion is
        // about AddOrderingInfrastructure's registration and not about a line
        // BuildHost copied from it.
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();

        DbContext resolved = scope.ServiceProvider.GetRequiredService<DbContext>();
        OrderingDbContext own = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();

        resolved.ShouldBeSameAs(own);
    }

    [Fact]
    public async Task The_scoped_inbox_read_returns_only_the_message_it_was_asked_for()
    {
        // The subject is the fixture's reader rather than the filter, and it is
        // owed for the reason every gate here is owed: a scoped read that
        // matched everything would make every assertion above pass vacuously,
        // on a green run, for as long as nobody looked. #166 measured what an
        // unscoped read costs; this is what shows the scoped one is looking at
        // the id it was handed.
        //
        // Everything rather than nothing, and the direction is what these two
        // rows are for. A reader matching nothing is already loud: the waits
        // above are `Eventually(..., expected: 1)` and `expected: 2`, and one
        // that never reaches its count spins out its hundred attempts and then
        // fails on an empty list. The mutation that passes in silence is the
        // opposite one — a dropped `Where`, or a predicate like
        // `m => m.MessageId != Guid.Empty` that never reads its argument —
        // because every test above runs after a ResetAsync that leaves its own
        // rows the only ones in the table, so a reader ignoring its argument
        // returns exactly what a correct one does. A second message staged
        // here is what stops that being true.
        //
        // Carried in both services' suites rather than homed in this one,
        // which is the opposite of the argument for the suppression counter
        // above. That property belongs to `InboxFilter<T>` — one assembly both
        // services reference, so one suite can hold it. `InboxAsync(Guid)` is
        // not common code: it is a fixture helper written once per service, so
        // this test cannot go red on Catalog's predicate and Catalog's cannot
        // go red on this one. Two implementations are two subjects, and
        // Catalog's copy is additionally the one §4.5's scaffold renders from;
        // this one covers the fixture nothing renders.
        //
        // Staged rather than consumed, because no endpoint has to run: what
        // must differ between the two rows is the MessageId, and the filter
        // only ever writes the one it was handed.
        var mine = Guid.CreateVersion7();
        var anotherMessage = Guid.CreateVersion7();

        await fixture.StageInboxAsync(
            new InboxMessage(mine, FirstEndpoint, DateTimeOffset.UtcNow),
            new InboxMessage(anotherMessage, FirstEndpoint, DateTimeOffset.UtcNow));

        (await fixture.InboxAsync(mine)).ShouldHaveSingleItem().MessageId.ShouldBe(mine);

        // Both ids, not a count. Asserting the table holds exactly two rows
        // would be the very claim about the whole schema this change exists to
        // stop making — and it would leave this test flaking on the leak it
        // was written to survive. What has to be true is that the unscoped
        // read saw the row the scoped one filtered out, which is what
        // distinguishes "the filter works" from "there was only ever one row".
        IReadOnlyList<Guid> all = [.. (await fixture.InboxAsync()).Select(r => r.MessageId)];

        all.ShouldContain(mine);
        all.ShouldContain(anotherMessage, "the scoped read filtered this row rather than never seeing it");
    }

    /// <summary>
    /// Polls until the expected row count appears. The harness confirms the
    /// message was consumed on <em>an</em> endpoint; with two endpoints running
    /// concurrently the second one's <c>SaveChangesAsync</c> may still be in
    /// flight, and a fixed wait would be a sleep §12.8 forbids.
    /// </summary>
    private static async Task<IReadOnlyList<T>> Eventually<T>(
        Func<Task<IReadOnlyList<T>>> read,
        int expected)
    {
        IReadOnlyList<T> rows = [];

        for (int attempt = 0; attempt < 100; attempt++)
        {
            rows = await read();

            if (rows.Count >= expected)
                return rows;

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        return rows;
    }
}
