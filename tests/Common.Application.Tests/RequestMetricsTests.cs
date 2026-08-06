using Shouldly;
using Xunit;

namespace Common.Application.Tests;

public class RequestMetricsTests
{
    [Fact]
    public void A_recorded_request_carries_its_name_and_its_outcome()
    {
        using TestMeterFactory factory = new();
        using MeasurementCollector collector = MeasurementCollector.ForRequests();
        RequestMetrics metrics = new(factory);

        metrics.Recorded("PlaceOrderCommand", "ok", TimeSpan.FromMilliseconds(1500));

        RecordedMeasurement measurement = collector.For("PlaceOrderCommand");

        measurement.Instrument.ShouldBe("request.duration");
        measurement.Tags["outcome"].ShouldBe("ok");
    }

    [Fact]
    public void A_duration_is_recorded_in_seconds()
    {
        // The unit is `s`, and §13.7's p95 targets are read in seconds. A
        // histogram fed milliseconds under a seconds unit is a dashboard that
        // is wrong by three orders of magnitude and looks fine.
        using TestMeterFactory factory = new();
        using MeasurementCollector collector = MeasurementCollector.ForRequests();
        RequestMetrics metrics = new(factory);

        metrics.Recorded("GetOrderSummariesQuery", "ok", TimeSpan.FromMilliseconds(1500));

        collector.For("GetOrderSummariesQuery").Value.ShouldBe(1.5);
    }
}
