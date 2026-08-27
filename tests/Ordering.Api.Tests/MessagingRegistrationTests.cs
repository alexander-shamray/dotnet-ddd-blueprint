using Common.Contracts.Catalog.V1;
using Common.Contracts.Inventory.V1;
using Common.Contracts.Ordering.V1;
using Common.Infrastructure.Messaging;
using Ordering.Application.Orders.CancelOrder;
using Ordering.Application.Orders.ConfirmOrder;
using Ordering.Application.Orders.FlagOrderForReview;
using Ordering.Application.Orders.MarkOrderShipped;
using Ordering.Infrastructure.Messaging;
using Ordering.Infrastructure.Persistence;
using System.Data;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using MassTransit.Middleware;
using MassTransit.Middleware.Outbox;
using MassTransit.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Ordering.Api.Tests;

/// <summary>
/// The harness smoke of Appendix C's messaging row, and the row's own split
/// says which half lives here: "publish/consume proven with the in-memory
/// harness" is this file, "bus connects" is the container suite's readiness
/// poll. <c>AddMassTransitTestHarness</c> replaces an existing
/// <c>AddMassTransit</c> bus with the in-memory transport — verified against
/// the 8.5.3 source — which means these tests prove the production helper
/// COMPOSES (its eager key read runs, its options land, nothing conflicts
/// with the consumer bindings) and that MassTransit's pipeline delivers.
/// What the swap deliberately removes is the <c>UsingRabbitMq</c> transport
/// configuration itself, so that half is asserted where it can be true: in
/// <c>DatabaseSmokeTests</c>, against a real broker.
/// </summary>
/// <remarks>
/// The message and consumer are test-local on purpose, and stay that way now
/// that <c>Common.Contracts</c> exists: this smoke needs a payload the
/// pipeline can carry, not a published contract other services may come to
/// depend on. A real contract here would make every change to it a change to
/// this test, and the test is about the registration rather than the message.
/// </remarks>
public class MessagingRegistrationTests
{
    /// <summary>
    /// The bus never dials this — the harness swaps the transport before
    /// start — but the helper's eager read still requires a value, and an
    /// unresolvable one means a test that accidentally reaches for the real
    /// transport fails loudly (§12.4's <c>.invalid</c> convention).
    /// </summary>
    private static IConfiguration Configuration(
        string? rabbitConnectionString = "amqp://guest:guest@ordering-rabbit.invalid:5672") =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(rabbitConnectionString is null
                ? []
                : [new KeyValuePair<string, string?>("ConnectionStrings:RabbitMq", rabbitConnectionString)])
            .Build();

    /// <summary>
    /// The bound that decides the assertions below, stated rather than
    /// inherited. It runs from the last bus activity, and MassTransit's
    /// default is 1.2 seconds — a developer machine's budget, not a statement
    /// about how long a saturated runner may take to schedule a consumer.
    /// </summary>
    /// <remarks>
    /// CI runs seven test assemblies concurrently, three of them starting
    /// Testcontainers, on two cores, and this test failed there and passed on
    /// a re-run of the same commit with no changes. 30 s is a generous
    /// scheduling budget for a smoke that asserts only positives and so never
    /// waits it out, while still failing a genuine composition defect in one
    /// bounded wait rather than hanging.
    /// </remarks>
    private static readonly TimeSpan HarnessInactivityTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The harness's other bound, stated for the same reason and deliberately
    /// larger. An assertion ends at the <em>earliest</em> applicable bound,
    /// not at the inactivity one alone — verified at the 8.5.3 pin, where
    /// <c>testTimeout: 2 s</c> against <c>testInactivityTimeout: 10 s</c> gave
    /// up after 2 s, and where this timeout is measured from the
    /// <c>Any(…)</c> call rather than from harness start. Leaving it inherited
    /// would let a number the test never states decide the wait, which is the
    /// defect this file exists to fix, one parameter over.
    /// </summary>
    /// <remarks>
    /// 60 s rather than a matching 30 s so that it never fires first: equal
    /// values would leave the two bounds racing, and which one reported a
    /// failure would be a detail of how long the publish took.
    /// </remarks>
    private static readonly TimeSpan HarnessTestTimeout = TimeSpan.FromSeconds(60);

    public sealed record ProbeMessage(Guid Id);

    public sealed class ProbeConsumer : IConsumer<ProbeMessage>
    {
        public Task Consume(ConsumeContext<ProbeMessage> context) => Task.CompletedTask;
    }

    /// <summary>
    /// One registration, shared by the smoke and by the guard that asserts its
    /// timeout — and shared deliberately. A guard building its own harness
    /// would keep passing with <c>SetTestTimeouts</c> deleted from the smoke,
    /// which is precisely the deletion it exists to catch.
    /// </summary>
    /// <remarks>
    /// <c>SetTestTimeouts</c> comes first because it is the only call in the
    /// chain returning <c>IBusRegistrationConfigurator</c>;
    /// <c>AddConsumer&lt;T&gt;</c> returns a consumer configurator, so the
    /// other order does not compile.
    /// </remarks>
    private static ServiceProvider BuildHarnessProvider()
    {
        ServiceCollection services = new();
        services.AddMassTransitMessaging(Configuration());
        services.AddMassTransitTestHarness(x => x
            .SetTestTimeouts(HarnessTestTimeout, HarnessInactivityTimeout)
            .AddConsumer<ProbeConsumer>());

        return services.BuildServiceProvider(validateScopes: true);
    }

    [Fact]
    public async Task The_harness_waits_for_the_stated_timeouts_rather_than_MassTransits_defaults()
    {
        // The defect this replaced was invisible from the smoke below: with
        // SetTestTimeouts deleted that test still passes on an idle machine
        // and fails only on a loaded runner, so a deletion would come back as
        // a flake rather than as a red test. Asserted here it fails at once —
        // as does a MassTransit bump that stops honouring the call.
        //
        // Both bounds, because the wait ends at whichever fires first: pinning
        // only the inactivity one would leave the other free to drop below it
        // and cap the wait without anything here going red.
        await using ServiceProvider provider = BuildHarnessProvider();

        ITestHarness harness = provider.GetRequiredService<ITestHarness>();

        harness.TestInactivityTimeout.ShouldBe(
            HarnessInactivityTimeout,
            "this is the bound that normally decides an unmatched assertion — 1.2s is MassTransit's " +
            "default and a developer machine's budget, not a saturated two-core runner's");
        harness.TestTimeout.ShouldBe(
            HarnessTestTimeout,
            "the assertion ends at the earliest applicable bound, so a TestTimeout below the " +
            "inactivity timeout would silently become the wait");
    }

    [Fact]
    public async Task Publish_reaches_a_consumer_with_the_transport_swapped_for_in_memory()
    {
        await using ServiceProvider provider = BuildHarnessProvider();

        ITestHarness harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var id = Guid.CreateVersion7();
        await harness.Bus.Publish(new ProbeMessage(id), TestContext.Current.CancellationToken);

        (await harness.Published.Any<ProbeMessage>(
            m => m.Context.Message.Id == id,
            TestContext.Current.CancellationToken)).ShouldBeTrue();
        (await harness.Consumed.Any<ProbeMessage>(
            m => m.Context.Message.Id == id,
            TestContext.Current.CancellationToken)).ShouldBeTrue(
            "the harness replaced the RabbitMQ transport, so a message that publishes but is never " +
            "consumed means the helper's registrations did not compose with the consumer bindings — " +
            "the transport configuration itself is DatabaseSmokeTests' claim, not this one's, and both " +
            "harness bounds are stated rather than inherited, so a busy runner is not the answer");
    }

    [Fact]
    public void Registration_adds_the_bus_and_its_hosted_service()
    {
        // Descriptors, not a built provider — the PR-12 shape. Building would
        // start nothing (the bus starts with the host), but a provider is a
        // heavier claim than the test makes.
        ServiceCollection services = new();

        services.AddMassTransitMessaging(Configuration());

        services.ShouldContain(d => d.ServiceType == typeof(IBus));
        services.ShouldContain(
            d => d.ServiceType == typeof(IHostedService),
            "MassTransit starts the bus from a hosted service; without it the registration is inert");
    }



    [Fact]
    public void Ordering_registers_exactly_the_consumers_its_chapters_grant()
    {
        // Every event and command §3.2 gives Ordering a CONSUMER for — the
        // set, not merely its size, because a subscription that quietly loses
        // one member is a handler that keeps compiling and stops being
        // invoked.
        //
        // Not the whole of §3.2's Consumes column, and the difference is the
        // saga: seven of the eleven events it lists reach Ordering ONLY through
        // the state machine's own correlation, which registers no IConsumer<>
        // at all. Naming this "Consumes and Accepts" stated a set equality the
        // list does not satisfy — a message that would have sent whoever read
        // it looking for seven missing registrations.
        //
        // **Seven and eight are two different counts and this comment used to
        // run them together.** The saga DECLARES eight events; seven of them
        // are missing from the list below. The eighth is StockReserved, which
        // has an IConsumer<> as well — two readers of one fact, argued at
        // StockEventsQueue.
        //
        // The eleventh is Ordering's own OrderCancelled, added when the saga
        // gained a cancellation branch (§9.6). It changes nothing here for
        // exactly the reason this paragraph exists: a saga event is not a
        // consumer registration, so the set below is the same eight it was.
        //
        // This asserts the REGISTRATION and deliberately not the endpoint:
        // AddMassTransitTestHarness replaces the UsingRabbitMq callback where
        // the receive endpoints live, so no test in this file can see a
        // binding. CatalogEventEndpointTests drives one over a real broker,
        // which is where a queue name and an inbox row are asserted.
        //
        // Until PR-20 this test read the other way round — that Ordering bound
        // no consumer at all — and carried a positive control beside it,
        // because a negative assertion over a predicate that matches nothing
        // passes for the wrong reason. A set equality cannot fail open that
        // way, so the control left with the absence it was guarding.
        //
        // PR-21 widened its SUBJECT rather than adding a second test beside
        // it, and that is the point worth keeping. The name said "for every
        // Catalog event", the assertion always read the whole registered set,
        // and the two agreed only while Catalog's three were the whole of it —
        // so the saga's five would have had to be excluded from a gate that
        // never excluded anything, or the gate split into a Catalog half that
        // stops covering the newest surface. This repository's most-repeated
        // failure is a gate that quietly narrows; the defence is a test whose
        // subject is what the gate is looking at.
        ServiceCollection services = new();

        services.AddMassTransitMessaging(Configuration());

        ConsumerTypes(services).ShouldBe(
            [
                // Catalog's whole Publishes column, which is Ordering's
                // subscription to it (§3.2) — the price projection's feed.
                typeof(IntegrationEventConsumer<ProductPublished>),
                typeof(IntegrationEventConsumer<PriceChanged>),
                typeof(IntegrationEventConsumer<ProductDiscontinued>),

                // Inventory's reservation, which the ORDER reacts to as well
                // as the saga: this consumer records it on the aggregate
                // (§5.4's ConfirmStock), and the saga reads the same fact
                // through its own correlation rather than through a consumer.
                typeof(IntegrationEventConsumer<StockReserved>),

                // §3.2's Accepts column, and exactly it. The saga sends four
                // commands to ordering-commands; a type missing here is one
                // sent into a queue that ignores it.
                typeof(CommandConsumer<CancelOrder, CancelOrderCommand>),
                typeof(CommandConsumer<ConfirmOrder, ConfirmOrderCommand>),
                typeof(CommandConsumer<MarkOrderShipped, MarkOrderShippedCommand>),
                typeof(CommandConsumer<FlagOrderForReview, FlagOrderForReviewCommand>)
            ],
            ignoreOrder: true,
            "these eight are every event and command §3.2 gives Ordering a CONSUMER for — a ninth is a " +
            "subscription no chapter grants, and a missing one is a handler that silently stops being " +
            "invoked. §3.2's Consumes column is longer: the seven fulfilment events reach the saga through " +
            "its own correlation rather than through an IConsumer<>, which is why they are absent here " +
            "and asserted by the harness suite instead");
    }

    [Fact]
    public void The_saga_is_registered_with_a_scheduler_behind_it()
    {
        // Two registrations that fail in the same silent way and are invisible
        // to every other test here: the harness replaces the transport, so a
        // saga bound to no endpoint and a scheduler never put on the pipeline
        // both look exactly like a working host until the first OrderPlaced.
        //
        // ADR-021 is the scheduler's argument; this is the line that notices
        // it leaving. IMessageScheduler is what AddDelayedMessageScheduler
        // registers, and the transport half — cfg.UseDelayedMessageScheduler —
        // cannot be asserted from a ServiceCollection at all, which is stated
        // rather than papered over: the saga suite in Ordering.Application.Tests
        // is what covers that half, by driving a state machine that arms a
        // schedule on its first message.
        ServiceCollection services = new();

        services.AddMassTransitMessaging(Configuration());

        services.ShouldContain(
            d => d.ServiceType == typeof(IMessageScheduler),
            "§9.6's four Schedule declarations need one, and nothing resolves a scheduler at startup — " +
            "without this line the first OrderPlaced faults onto the error queue (ADR-021)");

        services.ShouldContain(
            d => (d.ImplementationType ?? d.ServiceType) == typeof(OrderFulfilmentSaga),
            "the state machine itself — AddSagaStateMachine registers the machine as well as its instance");
    }

    [Fact]
    public void The_saga_has_a_transactional_outbox_rather_than_an_in_memory_one()
    {
        // **ADR-032, and the half of it a ServiceCollection can see.** The
        // filter that matters is UseEntityFrameworkOutbox on the saga's receive
        // endpoint, and a receive endpoint's filters cannot be read back from a
        // ServiceCollection at all — so this asserts the container half, and
        // OrderFulfilmentSagaEndpointTests asserts the filter itself against a
        // real broker by observing the row it writes. **Neither half implies
        // the other**, which is why both exist: AddEntityFrameworkOutbox with
        // no endpoint using it registers everything below and buffers nothing
        // durably, and an endpoint calling UseEntityFrameworkOutbox without it
        // throws at bus start.
        //
        // **The subject is the factory the filter resolves, and an earlier
        // revision of this test said it could not be.** That revision asserted
        // on InboxCleanupService<OrderingDbContext> and claimed
        // IOutboxContextFactory<OrderingDbContext> "is not public at the 8.5.3
        // pin" — false. It is public, in MassTransit.Middleware rather than the
        // MassTransit.Middleware.Outbox this file first guessed at, and the
        // claim was a failed using directive written up as a property of the
        // library. Compiling it is what settled it, which is this branch's own
        // lesson arriving a third time.
        //
        // The correction is not cosmetic. AddEntityFrameworkOutbox registers
        // the cleanup service behind a flag that o.DisableInboxCleanupService()
        // clears, so the old subject went red on a change that leaves the
        // outbox entirely intact — a gate keyed on the wrong thing. The factory
        // is what the endpoint filter actually resolves, and it is closed over
        // this service's own DbContext, so it cannot be satisfied by some other
        // context's outbox.
        //
        // Without that call the saga is back on UseInMemoryOutbox, whose buffer
        // flushes AFTER EntityFrameworkRepository commits — #128's window,
        // where a crash leaves the order in AwaitingPayment with stock
        // reserved, no AuthorisePayment sent, and the PaymentTimeout that would
        // have rescued it lost in the same buffer.
        ServiceCollection services = new();

        services.AddMassTransitMessaging(Configuration());

        services.ShouldContain(
            d => d.ServiceType == typeof(IOutboxContextFactory<OrderingDbContext>),
            "AddEntityFrameworkOutbox<OrderingDbContext> is what registers it, and nothing else here " +
            "does — its absence puts §9.6's saga back on the in-memory outbox (#128, ADR-032)");
    }

    [Fact]
    public void The_outbox_transaction_is_serializable_because_the_saga_repository_stopped_owning_it()
    {
        // **A regression ADR-032 created and this line is what caught it.**
        // ConcurrencyMode.Pessimistic exists so that two events arriving
        // concurrently for one CorrelationId serialise — the comment beside it
        // says exactly that — and it delivered it through the saga
        // repository's OWN transaction, opened at Serializable.
        //
        // Under ADR-032 that transaction is no longer the repository's. The
        // outbox filter opens one first and the repository joins it, so the
        // isolation level is now the OUTBOX's — and MassTransit defaults that
        // to RepeatableRead. Measured: without the line this pins, this
        // assertion reads RepeatableRead.
        //
        // The row-lock hint does not compensate. A pessimistic read takes
        // UPDLOCK on a row that EXISTS; the case Pessimistic is written for is
        // two deliveries that both take the Initially branch for a
        // CorrelationId with no row yet, where what is needed is a key-range
        // lock and only SERIALIZABLE takes one. Without it both find nothing,
        // both insert, and one loses on the primary key — recoverable, since
        // the outbox rolls back and the endpoint retries, but it is a fault
        // where there was none, on the exact property the mode was chosen for.
        //
        // Pinned rather than commented because nothing else can see it: the
        // value lives in an options object no test resolved before this one,
        // and the failure it prevents is a race that a green suite on an idle
        // machine says nothing about.
        ServiceCollection services = new();

        services.AddMassTransitMessaging(Configuration());

        using ServiceProvider provider = services.BuildServiceProvider();
        EntityFrameworkOutboxOptions<OrderingDbContext> options = provider
            .GetRequiredService<IOptions<EntityFrameworkOutboxOptions<OrderingDbContext>>>()
            .Value;

        options.IsolationLevel.ShouldBe(
            IsolationLevel.Serializable,
            "the outbox filter opens the consume transaction and the saga repository joins it, so " +
            "this value — not ConcurrencyMode.Pessimistic — is what serialises two deliveries " +
            "racing to create one instance (ADR-032)");
    }

    [Fact]
    public void The_outbox_columns_survive_this_contexts_string_convention()
    {
        // **A guarantee that rests on an internal helper of somebody else's
        // library, so it is asserted rather than believed.** OrderingDbContext
        // caps every string at 400 characters (§7.2), and MassTransit's three
        // entities are mapped by the library. What keeps a serialised message
        // out of an nvarchar(400) is that MassTransit's configurator CLEARS the
        // convention on its own properties before setting the two lengths it
        // wants — an internal call, not an explicit HasMaxLength on each
        // column. Nothing about a version bump has to announce that it stopped
        // doing so, and the failure would be silent until a message longer than
        // 400 characters was staged, which is every real one.
        //
        // The model rather than the database, deliberately: this is what EF
        // would emit, so it fails in the fast half rather than waiting for a
        // container — and a migration generated from a truncating model would
        // already be wrong before anything applied it.
        ServiceCollection services = new();

        services.AddDbContext<OrderingDbContext>(o => o.UseSqlServer("Server=.;Database=probe"));

        using ServiceProvider provider = services.BuildServiceProvider();
        using IServiceScope scope = provider.CreateScope();
        IModel model = scope.ServiceProvider.GetRequiredService<OrderingDbContext>().Model;

        IEntityType message = model.GetEntityTypes()
            .Single(t => t.ClrType.FullName == "MassTransit.EntityFrameworkCoreIntegration.OutboxMessage");

        foreach (string column in (string[])["Body", "Headers", "Properties", "MessageType"])
        {
            message.FindProperty(column)!.GetMaxLength().ShouldBeNull(
                $"{column} carries a serialised message and must stay nvarchar(max); this context's " +
                "400-character convention would truncate it, and MassTransit clears that convention " +
                "through an internal helper rather than by setting a length here (ADR-032)");
        }

        message.FindProperty("DestinationAddress")!.GetMaxLength().ShouldBe(
            256,
            "the addresses are the columns MassTransit does bound, and a 400 here would mean the " +
            "convention won after all");
    }

    [Fact]
    public void The_bus_side_outbox_is_deliberately_not_registered()
    {
        // **A decision pinned rather than a defect caught**, and it is the one
        // ADR-032 is most likely to be "corrected" on: UseBusOutbox() reads
        // like the obvious companion to the line above and is a different
        // mechanism. It intercepts IPublishEndpoint and ISendEndpointProvider
        // OUTSIDE a consume context — the API request path — which §9.4's
        // application outbox already owns, and where there is no dual write to
        // close. Adding it would put a third staging mechanism on that path.
        //
        // IBusOutboxNotification is registered by UseBusOutbox() and by nothing
        // else at the 8.5.3 pin, measured over a real ServiceCollection with
        // and without the call rather than read off the documentation.
        //
        // **A negative assertion passes when the name it looks for stops
        // existing**, so this one is not left on its own: the test above is its
        // positive control. If a MassTransit bump renamed either type, that one
        // goes red where this one would quietly keep agreeing.
        ServiceCollection services = new();

        services.AddMassTransitMessaging(Configuration());

        services.ShouldNotContain(
            d => d.ServiceType == typeof(IBusOutboxNotification),
            "UseBusOutbox() would register this. §9.4's outbox owns the request path, and a second " +
            "stage on a path with no dual write is cost without a guarantee (ADR-032)");
    }

    /// <summary>
    /// The consumer types MassTransit registered. The implementation type is
    /// what carries the interface — the service type is the consumer class
    /// itself — so this asks what the registered type implements rather than
    /// what it is registered as.
    /// </summary>
    /// <remarks>
    /// The distinction is not pedantry: an earlier version of this helper
    /// matched on <c>ServiceType</c> closing <c>IConsumer&lt;&gt;</c>, which
    /// MassTransit never registers — at the 8.5.3 pin <c>AddConsumer&lt;T&gt;</c>
    /// calls <c>TryAddScoped&lt;T&gt;()</c> on the concrete type — so it found
    /// nothing whether or not a consumer was present.
    /// </remarks>
    private static Type[] ConsumerTypes(IServiceCollection services) =>
        [.. services
            .Select(d => d.ImplementationType ?? d.ServiceType)
            .Where(t => Array.Exists(
                t.GetInterfaces(),
                i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IConsumer<>)))
            .Distinct()];

    [Fact]
    public void Usage_telemetry_is_disabled_by_the_production_registration_alone()
    {
        // Deliberately no harness: AddMassTransitTestHarness disables usage
        // telemetry itself (verified in the 8.5.3 source), so a harness-backed
        // assertion would stay green with the production line deleted — and
        // every real host would quietly resume reporting to the vendor.
        ServiceCollection services = new();
        services.AddMassTransitMessaging(Configuration());

        using ServiceProvider provider = services.BuildServiceProvider(validateScopes: true);

        provider
            .GetRequiredService<IOptions<UsageTelemetryOptions>>()
            .Value.Enabled.ShouldBeFalse("§13.2 owns this platform's telemetry, and none of it leaves silently");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_missing_or_blank_connection_string_fails_at_registration_naming_the_key(string? value)
    {
        // Eager, like AddSqlServer one folder over (§13.5): read lazily inside
        // UsingRabbitMq, the missing key would surface at bus start — after
        // the host is up, past ValidateOnBuild, in a background service's log.
        // Blank rows because an empty environment variable configures an empty
        // string, which a null-only guard waves through.
        ServiceCollection services = new();

        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() =>
            services.AddMassTransitMessaging(Configuration(rabbitConnectionString: value)));

        exception.Message.ShouldContain("ConnectionStrings:RabbitMq");
    }
}
