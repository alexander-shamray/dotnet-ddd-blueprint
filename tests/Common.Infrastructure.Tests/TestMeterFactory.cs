using System.Diagnostics.Metrics;

namespace Common.Infrastructure.Tests;

/// <summary>
/// The smallest <see cref="IMeterFactory"/> that satisfies a metrics class with
/// one constructor parameter. <c>AddMetrics</c> from
/// <c>Microsoft.Extensions.Diagnostics</c> would do the same and would mean a
/// package reference for a type these suites can write in ten lines.
/// </summary>
internal sealed class TestMeterFactory : IMeterFactory
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
    }
}
