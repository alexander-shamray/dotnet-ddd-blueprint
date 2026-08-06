using System.Diagnostics.Metrics;

namespace Common.Application;

/// <summary>
/// <c>request.duration</c> — the command and query p95 rows of §13.7. Lives in
/// Application because <see cref="LoggingBehavior{TRequest,TResult}"/> injects
/// it and the pipeline is Application (§13.3).
/// </summary>
/// <remarks>
/// Registered by a service's <c>Add&lt;Service&gt;Application</c> and forced at
/// startup like every other metrics type: "a behaviour injects it" is not the
/// same as "something has constructed it", and a service whose traffic has
/// stopped publishes nothing at all (§13.6).
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
