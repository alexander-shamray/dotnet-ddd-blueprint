using System.Diagnostics.Metrics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Instrumentation.AspNetCore;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using Shouldly;
using Xunit;

namespace Common.Web.Tests;

public class ObservabilityTests
{
    // The test holds its own copy of §13.2's list on purpose. Sharing a
    // constant with the registration would make this vacuous: deleting a name
    // from one place would delete it from the assertion too, and the whole
    // point is that an unregistered meter is collected by nothing and alerted
    // on in vain.
    private static readonly string[] Required =
    [
        "Ordering.Orders",
        "Ordering.Outbox",
        "Commerce.Requests",
        "Commerce.Messaging",
        "MassTransit",
        "Microsoft.Extensions.Caching.Hybrid",
        "StackExchange.Redis"
    ];

    [Fact]
    public void Every_meter_an_alert_reads_from_is_collected()
    {
        List<Metric> exported = [];

        HostApplicationBuilder builder = TelemetryHost.Builder();
        builder.AddObservability();
        builder.Services
            .AddOpenTelemetry()
            .WithMetrics(m => m.AddInMemoryExporter(exported));

        using IHost host = builder.Build();

        // Resolve the provider BEFORE creating any instrument. An instrument
        // created first has no listener, is never subscribed to, and the test
        // silently measures nothing while appearing to pass its setup.
        MeterProvider provider = host.Services.GetRequiredService<MeterProvider>();
        IMeterFactory factory = host.Services.GetRequiredService<IMeterFactory>();

        foreach (string name in Required)
            factory.Create(name).CreateCounter<long>("probe.counter").Add(1);

        provider.ForceFlush();

        // Asserted as a subset, not exact equality. AddRuntimeInstrumentation
        // and the OTLP exporter's own (failing, localhost) export attempt add
        // "System.Runtime", "System.Net.Http" and "System.Net.NameResolution"
        // to this list too — real .NET diagnostics meters that ARE genuinely
        // wanted in production, verified by removing each instrumentation call
        // in isolation and watching the corresponding meter disappear. An
        // exact match would only pass by dropping telemetry the same PR turns
        // on, which is a worse defect than the one this test guards against.
        Required.ShouldBeSubsetOf(exported.Select(m => m.MeterName).Distinct());
    }

    [Fact]
    public void The_resource_names_the_service_its_version_and_its_environment()
    {
        ResourceCapturingExporter exporter = new();

        HostApplicationBuilder builder = TelemetryHost.Builder();
        builder.AddObservability();
        builder.Services
            .AddOpenTelemetry()
            .WithMetrics(m => m.AddReader(new BaseExportingMetricReader(exporter)));

        using IHost host = builder.Build();

        MeterProvider provider = host.Services.GetRequiredService<MeterProvider>();
        host.Services
            .GetRequiredService<IMeterFactory>()
            .Create("Commerce.Requests")
            .CreateCounter<long>("probe.counter")
            .Add(1);
        provider.ForceFlush();

        Dictionary<string, object> attributes = exporter.Captured
            .ShouldNotBeNull()
            .Attributes
            .ToDictionary(a => a.Key, a => a.Value);

        attributes["service.name"].ShouldBe(TelemetryHost.ServiceName);
        attributes["service.version"].ShouldBe(BuildInfo.Version);
        attributes["deployment.environment"].ShouldBe(TelemetryHost.EnvironmentName);
    }

    [Fact]
    public void Health_probes_are_filtered_out_of_traces()
    {
        // Asserted on the registered predicate, not end to end. TestServer
        // produces no ASP.NET Core server spans at all, so an end-to-end
        // version of this test would pass while filtering nothing — verified
        // by probe before it was written this way.
        HostApplicationBuilder builder = TelemetryHost.Builder();
        builder.AddObservability();

        using IHost host = builder.Build();

        AspNetCoreTraceInstrumentationOptions options = host.Services
            .GetRequiredService<IOptionsMonitor<AspNetCoreTraceInstrumentationOptions>>()
            .Get(Options.DefaultName);

        options.Filter.ShouldNotBeNull();

        // At a ten-second probe interval across a dozen pods these would
        // otherwise dominate both trace volume and storage cost.
        options.Filter(Request("/health/live")).ShouldBeFalse();
        options.Filter(Request("/health/ready")).ShouldBeFalse();
        options.Filter(Request("/health/startup")).ShouldBeFalse();
        options.Filter(Request("/orders")).ShouldBeTrue();
    }

    private static DefaultHttpContext Request(string path)
    {
        DefaultHttpContext context = new();
        context.Request.Path = path;
        return context;
    }

    // ParentProvider.GetResource() is the only public route to the resource a
    // provider was configured with; no exported metric carries it.
    private sealed class ResourceCapturingExporter : BaseExporter<Metric>
    {
        public Resource? Captured { get; private set; }

        public override ExportResult Export(in Batch<Metric> batch)
        {
            Captured = ParentProvider?.GetResource();
            return ExportResult.Success;
        }
    }
}
