using Common.Infrastructure.Outbox;
using MassTransit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using Ordering.TestSupport;
using Ordering.TestSupport.Outbox;
using Shouldly;
using Xunit;

namespace Ordering.Api.Tests;

/// <summary>
/// §9.1's single-identity rule, checked where it is actually kept: on the
/// transport. Body, row, broker header and inbox key are one GUID, and
/// <c>DeliverAsync</c> copying the row's ids onto the published context is the
/// hop that makes the last two agree with the first two.
/// </summary>
/// <remarks>
/// <b>Catalog's twin, and the second copy is deliberate.</b> The dispatcher is
/// common code and this asserts a per-service <em>host</em>: it replaces
/// `IPublishEndpoint` inside a factory built over <c>Ordering.Api</c>'s
/// `Program`, and there is no way to write that once for two hosts. What
/// Catalog's copy cannot say is whether Ordering's registration reaches the
/// same code — which is exactly the question a second service exists to ask.
/// <para>
/// <b>It arrived with PR-21 rather than with the dispatcher</b>, because
/// staging a Broker row needs a contract this service publishes and §9.3's
/// allow-list was empty until the saga gave Ordering a reason to publish
/// <c>OrderPlaced</c>. Catalog's copy has covered the lines since PR-14; this
/// one covers the wiring under them.
/// </para>
/// <para>
/// A substitute for <c>IPublishEndpoint</c> rather than a harness: §12.4
/// refuses to bolt an <c>ITestHarness</c> onto this fixture, because it runs
/// the real host against the real broker on purpose and a harness would
/// replace the bus configuration the other tests exist to exercise. Capturing
/// the pipe costs one registration in one factory, disturbs nothing else, and
/// asserts the same thing.
/// </para>
/// </remarks>
[Collection(nameof(IntegrationCollection))]
public sealed class OutboxTransportIdentityTests(ServiceFixture fixture) : IAsyncLifetime
{
    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Delivery_copies_the_rows_ids_onto_the_published_context()
    {
        var orderId = Guid.CreateVersion7();
        OutboxMessage staged = OutboxRows.Broker(fixture, orderId);
        await fixture.StageOutboxAsync(staged);

        // A host of its own, so the substitute replaces the real endpoint for
        // this test and for nothing else in the collection.
        using CapturingPublishFactory factory = new(fixture.ConnectionString);
        OutboxDispatcher dispatcher = factory.Services.GetRequiredService<OutboxDispatcher>();

        (await dispatcher.ProcessBatchAsync(TestContext.Current.CancellationToken)).ShouldBe(1);

        // Replay the pipe the dispatcher handed the endpoint against a context
        // that records what is set on it. This is the callback's whole body.
        PublishContext context = Substitute.For<PublishContext>();
        await factory.Captured.ShouldNotBeNull().Send(context);

        context.Received().MessageId = staged.MessageId;
        context.Received().CorrelationId = staged.CorrelationId;
    }

    private sealed class CapturingPublishFactory(string connectionString) : OrderingApiFactory(
        connectionString,
        "amqp://guest:guest@ordering-rabbit.invalid:5672")
    {
        public IPipe<PublishContext>? Captured { get; private set; }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            builder.ConfigureServices(services =>
            {
                IPublishEndpoint endpoint = Substitute.For<IPublishEndpoint>();

                endpoint
                    .Publish(
                        Arg.Any<object>(),
                        Arg.Any<Type>(),
                        Arg.Any<IPipe<PublishContext>>(),
                        Arg.Any<CancellationToken>())
                    .Returns(call =>
                    {
                        Captured = call.Arg<IPipe<PublishContext>>();
                        return Task.CompletedTask;
                    });

                // Replaced, not added: the dispatcher resolves one endpoint,
                // and a second registration would leave MassTransit's real one
                // last and this substitute never called. The unreachable broker
                // in the base constructor is deliberate for the same reason —
                // nothing here should reach a transport.
                services.RemoveAll<IPublishEndpoint>();
                services.AddScoped(_ => endpoint);
            });
        }
    }
}
