using Catalog.Infrastructure.Persistence;
using Catalog.TestSupport;
using Common.Infrastructure.Inbox;
using MassTransit;
using MassTransit.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Catalog.Api.Tests;

/// <summary>
/// §9.5's inbox filter, over a real consume pipeline and the real table. The
/// in-memory transport rather than RabbitMQ, because what is under test is the
/// filter's own arithmetic — which of <c>MessageId</c> and <c>Endpoint</c> the
/// row is keyed on, and when it is committed — and both are properties of the
/// consume context rather than of the broker.
/// </summary>
/// <remarks>
/// Catalog binds no receive endpoint of its own (§3.2 gives it one Consumes
/// cell, owned by a service that does not exist), so this suite declares the
/// endpoints it needs. That is the same reason PR-14's <c>Local</c> lane was
/// proven by handlers in <c>Catalog.TestSupport</c>: the mechanism lands before
/// the first service that uses it, and inventing a consumer for Catalog would
/// be inventing a subscription §3.2 does not give it.
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
    /// registered <c>AddScoped&lt;DbContext, CatalogDbContext&gt;()</c> here
    /// would pass every assertion below while proving nothing about the
    /// transaction the filter is supposed to share.
    /// </summary>
    private ServiceProvider BuildHost<TConsumer>(bool withSecondEndpoint = false)
        where TConsumer : class, IConsumer<ProbeMessage>
    {
        ServiceCollection services = new();

        services.AddDbContext<CatalogDbContext>(o => o.UseSqlServer(fixture.ConnectionString));
        services.AddScoped<DbContext>(sp => sp.GetRequiredService<CatalogDbContext>());

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
        for (int delivery = 0; delivery < 2; delivery++)
        {
            await harness.Bus.Publish(
                new ProbeMessage(id),
                c => c.MessageId = messageId,
                TestContext.Current.CancellationToken);

            (await harness.Consumed.Any<ProbeMessage>(TestContext.Current.CancellationToken))
                .ShouldBeTrue();
        }

        FirstConsumer.Consumed.ShouldBe([id]);
        (await fixture.InboxAsync()).ShouldHaveSingleItem().MessageId.ShouldBe(messageId);
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
        IReadOnlyList<InboxMessage> rows = await Eventually(() => fixture.InboxAsync(), expected: 2);

        rows.Select(r => r.Endpoint).OrderBy(e => e).ShouldBe([FirstEndpoint, SecondEndpoint]);
        rows.ShouldAllBe(r => r.MessageId == messageId);
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

        await harness.Bus.Publish(
            new ProbeMessage(Guid.CreateVersion7()),
            c => c.MessageId = Guid.CreateVersion7(),
            TestContext.Current.CancellationToken);

        (await harness.Consumed.Any<ProbeMessage>(TestContext.Current.CancellationToken)).ShouldBeTrue();

        (await fixture.InboxAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task The_filters_context_is_the_services_own_instance()
    {
        // AddScoped<DbContext, CatalogDbContext>() compiles, resolves and is
        // wrong: it builds a SECOND context in the same scope, so the inbox row
        // commits in its own transaction and §9.5's atomic row silently becomes
        // its non-atomic one. Nothing fails — the guarantee just stops holding,
        // which is why it is asserted rather than reviewed.
        //
        // Read from the real host, not from this suite's, so the assertion is
        // about AddCatalogInfrastructure's registration and not about a line
        // BuildHost copied from it.
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();

        DbContext resolved = scope.ServiceProvider.GetRequiredService<DbContext>();
        CatalogDbContext own = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();

        resolved.ShouldBeSameAs(own);
    }

    /// <summary>
    /// Polls until the expected row count appears. The harness confirms the
    /// message was consumed on <em>an</em> endpoint; with two endpoints running
    /// concurrently the second one's <c>SaveChangesAsync</c> may still be in
    /// flight, and a fixed wait would be a sleep §12.8 forbids.
    /// </summary>
    private static async Task<IReadOnlyList<InboxMessage>> Eventually(
        Func<Task<IReadOnlyList<InboxMessage>>> read,
        int expected)
    {
        IReadOnlyList<InboxMessage> rows = [];

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
