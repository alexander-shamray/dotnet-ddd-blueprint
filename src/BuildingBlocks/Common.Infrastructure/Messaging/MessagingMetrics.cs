using System.Diagnostics.Metrics;

namespace Common.Infrastructure.Messaging;

/// <summary>
/// §13.3's messaging instruments, on the <c>Commerce.Messaging</c> meter that
/// <c>AddObservability</c> already collects. Infrastructure rather than
/// Application because every call site is — the outbox dispatcher's invoker
/// today, two consumers at PR-15.
/// </summary>
/// <remarks>
/// <b>One instrument of three, and the other two join with their call sites.</b>
/// §13.3 describes the finished class: <c>messaging.delivery.lag</c>, recorded
/// by <c>IntegrationEventConsumer&lt;T&gt;</c>, and the
/// <c>command.domain_rejected</c> counter <c>CommandConsumer&lt;,&gt;</c>
/// writes (§9.8), both arriving with PR-15's consumers. This is the
/// <c>PluggableInterfaces.All</c> shape — a type the blueprint describes whole
/// and the code grows in instalments — and it is preferred to landing two
/// instruments nothing records, which is an assertion that a signal exists
/// when the dashboard reading it would show an empty series.
/// <para>
/// The lag compares a timestamp made on another machine, so it is useful at
/// second granularity and meaningless below it — which is why §13.7's target
/// for it is in seconds.
/// </para>
/// </remarks>
public sealed class MessagingMetrics
{
    private readonly Histogram<double> _projectionLag;

    public MessagingMetrics(IMeterFactory factory)
    {
        Meter meter = factory.Create("Commerce.Messaging");

        _projectionLag = meter.CreateHistogram<double>(
            "projection.lag",
            unit: "s",
            description: "Event raised to projection applied.");
    }

    public void Projected(string message, TimeSpan lag) =>
        _projectionLag.Record(lag.TotalSeconds, new KeyValuePair<string, object?>("message", message));
}
