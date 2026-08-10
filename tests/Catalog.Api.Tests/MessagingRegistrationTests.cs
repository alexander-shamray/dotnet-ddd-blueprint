using Catalog.Infrastructure.Messaging;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Xunit;

namespace Catalog.Api.Tests;

/// <summary>
/// The harness smoke of Appendix C's messaging row: publish/consume proven
/// through the production registration with the transport swapped for
/// in-memory. <c>AddMassTransitTestHarness</c> replaces an existing
/// <c>AddMassTransit</c> bus with the in-memory transport — verified against
/// the 8.5.3 source, and the reason these tests can drive
/// <c>AddMassTransitMessaging</c> itself rather than a parallel registration
/// that would prove nothing about it.
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
    private static IConfiguration Configuration(string? rabbitConnectionString =
        "amqp://guest:guest@catalog-rabbit.invalid:5672") =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(rabbitConnectionString is null
                ? []
                : [new KeyValuePair<string, string?>("ConnectionStrings:RabbitMq", rabbitConnectionString)])
            .Build();

    public sealed record ProbeMessage(Guid Id);

    public sealed class ProbeConsumer : IConsumer<ProbeMessage>
    {
        public Task Consume(ConsumeContext<ProbeMessage> context) => Task.CompletedTask;
    }

    [Fact]
    public async Task Publish_reaches_a_consumer_through_the_production_registration()
    {
        ServiceCollection services = new();
        services.AddMassTransitMessaging(Configuration());
        services.AddMassTransitTestHarness(x => x.AddConsumer<ProbeConsumer>());

        await using ServiceProvider provider = services.BuildServiceProvider(validateScopes: true);

        ITestHarness harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var id = Guid.CreateVersion7();
        await harness.Bus.Publish(new ProbeMessage(id), TestContext.Current.CancellationToken);

        (await harness.Published.Any<ProbeMessage>(m => m.Context.Message.Id == id,
            TestContext.Current.CancellationToken)).ShouldBeTrue();
        (await harness.Consumed.Any<ProbeMessage>(m => m.Context.Message.Id == id,
            TestContext.Current.CancellationToken)).ShouldBeTrue(
            "the harness replaced the RabbitMQ transport, so a message that publishes but is never " +
            "consumed means the production registration did not compose with the consumer bindings");
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
    public void A_missing_connection_string_fails_at_registration_naming_the_key()
    {
        // Eager, like AddSqlServer one folder over (§13.5): read lazily inside
        // UsingRabbitMq, the missing key would surface at bus start — after
        // the host is up, past ValidateOnBuild, in a background service's log.
        ServiceCollection services = new();

        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() =>
            services.AddMassTransitMessaging(Configuration(rabbitConnectionString: null)));

        exception.Message.ShouldContain("ConnectionStrings:RabbitMq");
    }
}
