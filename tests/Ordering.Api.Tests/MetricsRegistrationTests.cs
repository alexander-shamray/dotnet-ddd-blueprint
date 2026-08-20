using System.Diagnostics.Metrics;
using Ordering.Application;
using Ordering.Infrastructure;
using Ordering.Infrastructure.Observability;
using Common.Application;
using Common.Infrastructure.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Xunit;

namespace Ordering.Api.Tests;

/// <summary>
/// §13.6's registration rules, which are the ones <c>ValidateOnBuild</c> cannot
/// check: nothing depends on a metrics class, so a container is perfectly happy
/// without one and the instruments simply never exist.
/// </summary>
/// <remarks>
/// No container and no <see cref="IntegrationCollection"/> — every assertion
/// here is about a <c>ServiceCollection</c> or about a <see cref="Meter"/>, so
/// these run in the fast half (<c>Category!=Integration</c>). The gauges' SQL
/// is proven one file over, against a real engine, where it can be.
/// </remarks>
public class MetricsRegistrationTests
{
    /// <summary>
    /// Types deliberately not forced, each with the reason it does not need to
    /// be. Empty today, and that is the point: a name lands here only when
    /// somebody argues it in a pull request.
    /// </summary>
    private static readonly Dictionary<Type, string> NotForced = [];

    [Fact]
    public void Every_metrics_type_is_forced_or_has_a_stated_reason_not_to_be()
    {
        // The COLLECTION, not a built provider. IServiceCollection is the input
        // to BuildServiceProvider and is not itself a registered service, so
        // asking a provider for one throws — registrations cannot be enumerated
        // after the build.
        //
        // It runs BOTH helpers, which matters here and nowhere else: the types
        // are split across AddOrderingApplication (RequestMetrics) and
        // AddOrderingInfrastructure (OutboxMetrics, MessagingMetrics). A helper
        // that ran only one half would see a subset and fail against a correct
        // MetricsInitialiser — the test reporting a defect in the thing it is
        // guarding.
        Type[] registered =
        [
            .. BuildServices()
                .Select(d => d.ServiceType)
                .Where(t => t.Name.EndsWith("Metrics", StringComparison.Ordinal))
                .Distinct()
        ];

        HashSet<Type> forced =
        [
            .. typeof(MetricsInitialiser)
                .GetConstructors()
                .Single()
                .GetParameters()
                .Select(p => p.ParameterType)
        ];

        // Both directions. Unforced-and-unexplained is the drift this exists
        // for; forced-but-unregistered is a host that will not start.
        registered
            .Where(t => !forced.Contains(t) && !NotForced.ContainsKey(t))
            .ShouldBeEmpty("add it to MetricsInitialiser, or to NotForced with a reason");

        forced.ShouldBeSubsetOf(registered);
    }

    /// <summary>
    /// The subject of the test above is what it is <i>looking at</i>, and this
    /// is that assertion. A selector that silently matched nothing would pass
    /// both directions vacuously — the repeated failure this repository names
    /// in <c>CLAUDE.md</c> — so the candidate set is asserted to be non-empty
    /// and to hold the one type this pull request added.
    /// </summary>
    [Fact]
    public void The_metrics_selector_actually_selects_something()
    {
        Type[] registered =
        [
            .. BuildServices()
                .Select(d => d.ServiceType)
                .Where(t => t.Name.EndsWith("Metrics", StringComparison.Ordinal))
                .Distinct()
        ];

        registered.ShouldContain(typeof(OutboxMetrics));
        registered.ShouldContain(typeof(MessagingMetrics));
        registered.ShouldContain(typeof(RequestMetrics));
    }

    [Fact]
    public void The_initialiser_is_registered_as_a_hosted_service()
    {
        // The registration is the whole mechanism: without it the singletons
        // above are lazy, nothing resolves them, and the instruments do not
        // exist. ImplementationType rather than a resolve, because a resolve
        // would also pass if some other line had constructed the type.
        BuildServices()
            .Where(d => d.ServiceType == typeof(IHostedService))
            .Select(d => d.ImplementationType)
            .ShouldContain(typeof(MetricsInitialiser));
    }

    [Fact]
    public void The_outbox_gauges_report_one_measurement_per_lane_on_the_registered_meter()
    {
        StubOutboxStats stats = new();
        List<(string Instrument, double Value, string Lane)> collected = [];

        using MeterListener listener = new();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == OutboxMetrics.MeterName)
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            collected.Add((instrument.Name, value, LaneOf(tags))));

        // The factory has to outlive the collection: a Meter disposed with its
        // factory publishes nothing.
        using IMeterFactory factory = new ServiceCollection()
            .AddMetrics()
            .BuildServiceProvider()
            .GetRequiredService<IMeterFactory>();

        OutboxMetrics metrics = new(factory, stats);
        metrics.ShouldNotBeNull();
        listener.Start();
        listener.RecordObservableInstruments();

        // Three instruments, two lanes, and the tag spelled the way the Lane
        // column stores it — §9.4's dispatcher compares against "Broker", so a
        // lowercase tag would give one value three spellings across SQL, C# and
        // PromQL and every alert would match no series at all.
        collected.Select(m => m.Instrument).Distinct().ShouldBe(
            ["outbox.oldest.age", "outbox.pending.count", "outbox.abandoned.count"],
            ignoreOrder: true);
        collected.Select(m => m.Lane).Distinct().ShouldBe(["Broker", "Local"], ignoreOrder: true);

        // Every lane the enum declares, not a list written out in the gauge. A
        // lane added and forgotten would be a lane with no gauge and therefore
        // no alert.
        collected
            .Select(m => m.Lane)
            .Distinct()
            .ShouldBe(Enum.GetNames<OutboxLane>(), ignoreOrder: true);

        // The values come from IOutboxStats rather than from anywhere else,
        // per lane and per instrument — six readings, six distinct numbers.
        collected
            .Single(m => m.Instrument == "outbox.oldest.age" && m.Lane == nameof(OutboxLane.Broker))
            .Value.ShouldBe(11);
        collected
            .Single(m => m.Instrument == "outbox.abandoned.count" && m.Lane == nameof(OutboxLane.Local))
            .Value.ShouldBe(62);
    }

    private static string LaneOf(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        foreach (KeyValuePair<string, object?> tag in tags)
        {
            if (tag.Key == "lane")
                return tag.Value?.ToString() ?? "";
        }

        return "";
    }

    /// <summary>
    /// Both halves of the service's registration, over configuration that
    /// reaches nothing. The .invalid convention of §12.4: no host runs here, so
    /// nothing should be able to dial a real dependency.
    /// </summary>
    private static ServiceCollection BuildServices()
    {
        ServiceCollection services = new();
        services.AddOrderingApplication();
        services.AddOrderingInfrastructure(new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Ordering"] =
                        "Server=ordering-sql.invalid;Database=Ordering;User Id=sa;Password=not-a-real-password",
                    ["ConnectionStrings:RabbitMq"] = "amqp://guest:guest@ordering-rabbit.invalid:5672"
                })
            .Build());

        return services;
    }

    /// <summary>
    /// A distinct number per lane and per question, so an assertion cannot pass
    /// by reading the right value off the wrong call.
    /// </summary>
    private sealed class StubOutboxStats : IOutboxStats
    {
        public double OldestAgeSeconds(OutboxLane lane) => lane == OutboxLane.Broker ? 11 : 12;

        public int PendingCount(OutboxLane lane) => lane == OutboxLane.Broker ? 41 : 42;

        public int AbandonedCount(OutboxLane lane) => lane == OutboxLane.Broker ? 61 : 62;
    }
}
