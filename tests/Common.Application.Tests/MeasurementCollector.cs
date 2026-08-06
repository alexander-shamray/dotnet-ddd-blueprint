using System.Diagnostics.Metrics;

namespace Common.Application.Tests;

/// <summary>One recorded measurement, with the tags flattened for assertion.</summary>
public sealed record RecordedMeasurement(
    string Instrument,
    double Value,
    IReadOnlyDictionary<string, object?> Tags);

/// <summary>
/// An <see cref="IMeterFactory"/> that owns the meters it hands out, so a test
/// that disposes it takes its instruments with it. The real one comes from
/// <c>AddMetrics</c>, which lives on the host side of §4.2 and is therefore not
/// something <c>Common.Application</c> can reach for.
/// </summary>
public sealed class TestMeterFactory : IMeterFactory
{
    private readonly List<Meter> _meters = [];

    public Meter Create(MeterOptions options)
    {
        Meter meter = new(options);
        _meters.Add(meter);
        return meter;
    }

    public void Dispose()
    {
        foreach (Meter meter in _meters)
            meter.Dispose();

        _meters.Clear();
    }
}

/// <summary>
/// Everything recorded on one meter, read through a <see cref="MeterListener"/>
/// rather than a testing package. §13.3's assertion is about the instrument
/// name, the value and the two tags — which is exactly what the listener hands
/// over, and the whole of what a collector would wrap.
/// </summary>
public sealed class MeasurementCollector : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly List<RecordedMeasurement> _measurements = [];

    public MeasurementCollector(string meterName)
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == meterName)
                listener.EnableMeasurementEvents(instrument);
        };

        _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
        {
            Dictionary<string, object?> flattened = [];

            foreach (KeyValuePair<string, object?> tag in tags)
                flattened[tag.Key] = tag.Value;

            lock (_measurements)
                _measurements.Add(new RecordedMeasurement(instrument.Name, value, flattened));
        });

        _listener.Start();
    }

    /// <summary>
    /// Listening on the meter §13.3 gives <c>RequestMetrics</c>. The name is
    /// spelled out again here rather than read off a constant in the source:
    /// it is the string a dashboard queries, so a test that took it from the
    /// class under test would agree with that class however it drifted.
    /// </summary>
    public static MeasurementCollector ForRequests() => new("Commerce.Requests");

    public IReadOnlyList<RecordedMeasurement> Measurements
    {
        get
        {
            lock (_measurements)
                return [.. _measurements];
        }
    }

    /// <summary>
    /// The one measurement tagged with this request. Filtering by tag rather
    /// than taking the only measurement, because the meter is process-wide and
    /// a test class running beside this one records to it too.
    /// </summary>
    public RecordedMeasurement For(string request) =>
        Measurements.Single(m => m.Tags.TryGetValue("request", out object? value) && Equals(value, request));

    public void Dispose() => _listener.Dispose();
}
