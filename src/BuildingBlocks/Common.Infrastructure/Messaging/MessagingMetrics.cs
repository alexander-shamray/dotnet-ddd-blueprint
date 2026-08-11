using System.Diagnostics.Metrics;

namespace Common.Infrastructure.Messaging;

/// <summary>
/// §13.3's messaging instruments, on the <c>Commerce.Messaging</c> meter that
/// <c>AddObservability</c> already collects. Infrastructure rather than
/// Application because every call site is — the outbox dispatcher's invoker and
/// the two consumers beside this file.
/// </summary>
/// <remarks>
/// <b>Complete since PR-15, and it arrived in instalments on
/// <c>PluggableInterfaces.All</c>'s terms.</b> <c>Projected</c> landed with the
/// outbox because <c>ProjectionInvoker</c> is its only call site, and the other
/// two with the consumers that record them — two instruments nothing writes to
/// would be an empty series on a dashboard rather than a signal.
/// <para>
/// <b>The two lags read <c>OccurredAt</c> from different places, because they
/// measure different lanes.</b> <see cref="Delivered"/> reads it off the
/// message: it covers the broker lane, every integration event carries the
/// field (§9.1), and <c>IntegrationEventConsumer&lt;T&gt;</c> reaches it through
/// the <c>IIntegrationEvent</c> constraint. <see cref="Projected"/> reads it off
/// the outbox row, which the claim returns and which <c>Stage</c> copies from
/// the message rather than from a clock — it has to, because the local lane
/// carries domain events and <c>ProjectionInvoker</c> is deliberately
/// unconstrained.
/// </para>
/// <para>
/// Both lags compare a timestamp made on another machine, so both are useful at
/// second granularity and meaningless below it — which is why §13.7's targets
/// for them are in seconds. <see cref="Rejected"/> carries no such caveat: it
/// is recorded at the moment the dispatcher returns a failure, on the same
/// machine.
/// </para>
/// </remarks>
public sealed class MessagingMetrics
{
    private readonly Histogram<double> _deliveryLag;
    private readonly Histogram<double> _projectionLag;
    private readonly Counter<long> _rejected;

    public MessagingMetrics(IMeterFactory factory)
    {
        Meter meter = factory.Create("Commerce.Messaging");

        _deliveryLag = meter.CreateHistogram<double>(
            "messaging.delivery.lag",
            unit: "s",
            description: "OccurredAt to consumer start.");
        _projectionLag = meter.CreateHistogram<double>(
            "projection.lag",
            unit: "s",
            description: "Event raised to projection applied.");
        _rejected = meter.CreateCounter<long>(
            "command.domain_rejected",
            description: "Message-borne commands the domain refused (§9.8).");
    }

    public void Delivered(string message, TimeSpan lag) =>
        _deliveryLag.Record(lag.TotalSeconds, new KeyValuePair<string, object?>("message", message));

    public void Projected(string message, TimeSpan lag) =>
        _projectionLag.Record(lag.TotalSeconds, new KeyValuePair<string, object?>("message", message));

    /// <summary>
    /// A command the domain refused (§9.8). Feeds no SLO row — it is a business
    /// signal that happens to share this meter, and it belongs on a dashboard
    /// rather than on a pager.
    /// </summary>
    /// <remarks>
    /// <b><paramref name="error"/> is an <c>Error.Code</c> and nothing else.</b>
    /// It is tempting to reach for a cancellation reason here, and the two
    /// vocabularies describe opposite events: a payment-declined cancellation is
    /// a command the domain <em>accepted</em>, so it can never appear on a
    /// counter of commands it refused. Both are lowercase snake_case strings on
    /// a counter tagged <c>error</c>, which is what makes the mistake invisible
    /// in a dashboard. <c>Error.Code</c> is also a closed set by construction
    /// (§10.5), so the tag cannot explode.
    /// </remarks>
    public void Rejected(string message, string error) =>
        _rejected.Add(
            1,
            new KeyValuePair<string, object?>("message", message),
            new KeyValuePair<string, object?>("error", error));
}
