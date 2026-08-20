using System.Diagnostics.Metrics;

namespace Common.Application;

/// <summary>
/// <c>request.duration</c> — the command and query p95 rows of §13.7. Lives in
/// Application because <see cref="LoggingBehavior{TRequest,TResult}"/> injects
/// it and the pipeline is Application (§13.3).
/// </summary>
/// <remarks>
/// Registered by a service's <c>Add&lt;Service&gt;Application</c>, and forced at
/// startup wherever a <c>MetricsInitialiser</c> exists: "a behaviour injects it"
/// is not the same as "something has constructed it", and a service whose
/// traffic has stopped publishes nothing at all (§13.6).
/// <para>
/// <b>Ordering forces it and Catalog does not</b>, because §13.3 puts
/// <c>MetricsInitialiser</c> beside <c>OutboxMetrics</c> in
/// <c>Ordering.Infrastructure</c>. That is a real gap rather than a nuance —
/// it is named in §13.6 and asserted by <c>deploy/observability/check.py</c>,
/// which refuses to let a service host the outbox dispatcher without either
/// publishing the gauges or carrying a stated exemption. This remark used to
/// say "like every other metrics type", which described the target rather than
/// the solution.
/// </para>
/// </remarks>
public sealed class RequestMetrics
{
    private readonly Histogram<double> _duration;

    public RequestMetrics(IMeterFactory factory)
    {
        Meter meter = factory.Create("Commerce.Requests");
        _duration = meter.CreateHistogram<double>(
            "request.duration",
            unit: "s",
            description: "Dispatcher entry to result.");
    }

    public void Recorded(string request, string outcome, TimeSpan elapsed) =>
        _duration.Record(
            elapsed.TotalSeconds,
            new KeyValuePair<string, object?>("request", request),
            new KeyValuePair<string, object?>("outcome", outcome));
}
