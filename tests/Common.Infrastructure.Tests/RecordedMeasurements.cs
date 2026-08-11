using System.Diagnostics.Metrics;

namespace Common.Infrastructure.Tests;

/// <summary>
/// Every measurement one meter published while this object was alive, with its
/// tags. A <see cref="MeterListener"/> rather than a substituted
/// <c>MessagingMetrics</c>, because the assertion worth making is that the
/// instrument carries the right <em>name and tags</em> — a fake would prove only
/// that a method was called, and the tag vocabulary is what §9.8 and §13.3
/// actually constrain.
/// </summary>
/// <remarks>
/// Scoped to one meter name and disposed by the test, so a listener does not
/// outlive the class that made it and collect another's measurements — the same
/// ambient-state hazard <c>Common.Web.Tests</c> disables parallelism for.
/// <para>
/// <b>Construct this before the instruments exist.</b> <c>InstrumentPublished</c>
/// fires when an instrument is created, so a listener started after
/// <c>MessagingMetrics</c> has been resolved subscribes to nothing and the test
/// reads an empty list as "nothing was recorded".
/// </para>
/// </remarks>
internal sealed class RecordedMeasurements : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly List<Measurement> _taken = [];
    private readonly Lock _gate = new();

    public RecordedMeasurements(string meterName)
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == meterName)
                listener.EnableMeasurementEvents(instrument);
        };

        // Both callbacks, because the meter carries histograms of double and a
        // counter of long — a listener registered for one shape silently drops
        // the other.
        _listener.SetMeasurementEventCallback<double>(
            (instrument, value, tags, _) => Record(instrument, value, tags));
        _listener.SetMeasurementEventCallback<long>(
            (instrument, value, tags, _) => Record(instrument, value, tags));

        _listener.Start();
    }

    internal sealed record Measurement(string Instrument, double Value, KeyValuePair<string, object?>[] Tags)
    {
        public string? Tag(string name) =>
            Tags.FirstOrDefault(t => t.Key == name).Value?.ToString();
    }

    /// <summary>Every measurement one instrument took, in order.</summary>
    public IReadOnlyList<Measurement> For(string instrument)
    {
        lock (_gate)
            return [.. _taken.Where(m => m.Instrument == instrument)];
    }

    public void Dispose() => _listener.Dispose();

    private void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        // The span cannot escape the callback, so it is copied before the lock
        // rather than inside it. A spread, not a terminal ToArray(): the target
        // type is on the left, which is the whole of that rule's argument, and
        // a collection expression takes a ReadOnlySpan source like any other.
        KeyValuePair<string, object?>[] copied = [.. tags];

        lock (_gate)
            _taken.Add(new Measurement(instrument.Name, value, copied));
    }
}
