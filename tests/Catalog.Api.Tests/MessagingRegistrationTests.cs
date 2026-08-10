using Catalog.Infrastructure.Messaging;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Catalog.Api.Tests;

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
/// The message and consumer are test-local on purpose.
/// <c>Common.Contracts</c> does not exist until PR-15, and a message type
/// invented early is a project invented early — the smoke needs a payload the
/// pipeline can carry, not a contract anything else may come to depend on.
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
        string? rabbitConnectionString = "amqp://guest:guest@catalog-rabbit.invalid:5672") =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(rabbitConnectionString is null
                ? []
                : [new KeyValuePair<string, string?>("ConnectionStrings:RabbitMq", rabbitConnectionString)])
            .Build();

    /// <summary>
    /// Stated rather than inherited, because MassTransit's default is 1.2
    /// seconds and that default is the bound the consume assertion below
    /// actually waits on. Verified against the 8.5.3 pin rather than
    /// remembered: a message with no consumer gave up after 1.2 s with
    /// <c>testTimeout</c> left alone, after 1.2 s again with
    /// <c>testTimeout</c> lowered to 3 s, and after 5 s when this value alone
    /// was raised to 5 s. So <c>SetTestTimeouts(testTimeout: …)</c> is not the
    /// fix it looks like — it moves a number nothing here reads.
    /// </summary>
    /// <remarks>
    /// 1.2 s is a developer machine's budget, not a statement about how long
    /// a saturated runner may take to schedule a consumer: CI runs seven test
    /// assemblies concurrently, three of them starting Testcontainers, on two
    /// cores — and this test failed there and passed on a re-run of the same
    /// commit with no changes. 30 s is MassTransit's own <c>TestTimeout</c>
    /// default, so the wait now ends where the harness's outer bound would
    /// rather than 25× earlier, and a genuine composition failure still fails
    /// in one bounded wait instead of hanging.
    /// </remarks>
    private static readonly TimeSpan HarnessInactivityTimeout = TimeSpan.FromSeconds(30);

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
            .SetTestTimeouts(testInactivityTimeout: HarnessInactivityTimeout)
            .AddConsumer<ProbeConsumer>());

        return services.BuildServiceProvider(validateScopes: true);
    }

    [Fact]
    public async Task The_harness_waits_for_the_stated_timeout_rather_than_MassTransits_default()
    {
        // The defect this replaced was invisible from the smoke below: with
        // SetTestTimeouts deleted that test still passes on an idle machine
        // and fails only on a loaded runner, so a deletion would come back as
        // a flake rather than as a red test. Asserted here it fails at once —
        // as does a MassTransit bump that stops honouring the call.
        await using ServiceProvider provider = BuildHarnessProvider();

        provider
            .GetRequiredService<ITestHarness>()
            .TestInactivityTimeout.ShouldBe(
                HarnessInactivityTimeout,
                "Consumed.Any waits on the inactivity timeout and never on TestTimeout, so this is the " +
                "one of the two that has to be stated — 1.2s is MassTransit's default and a developer " +
                "machine's budget, not a saturated two-core runner's");
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
            "the transport configuration itself is DatabaseSmokeTests' claim, not this one's, and the " +
            "wait is HarnessInactivityTimeout rather than a default, so a busy runner is not the answer");
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
