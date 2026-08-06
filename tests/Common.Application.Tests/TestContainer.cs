using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Common.Application.Tests;

/// <summary>
/// The registration path every test in this assembly goes through, standing in
/// for §4.2's <c>AddOrderingApplication</c>. One builder rather than a container
/// per test: §6.2's argument for testing the real path is that a hand-built
/// container asserts what the test remembered to register, and the omission it
/// is meant to catch is exactly the one a hand-built container reproduces.
/// </summary>
internal static class TestContainer
{
    internal static ServiceProvider Build(Action<IServiceCollection>? behaviours = null)
    {
        ServiceCollection services = new();

        services.AddDispatcher();
        services.AddPluggableFrom(typeof(Ping).Assembly);

        // The clock, as one instance under two service types — FakeTimeProvider
        // for the handler that advances it, TimeProvider for the behaviour that
        // reads it. Two registrations of the same class would give them two
        // clocks and the elapsed time would always be zero.
        services.AddSingleton<FakeTimeProvider>();
        services.AddSingleton<TimeProvider>(sp => sp.GetRequiredService<FakeTimeProvider>());

        services.AddSingleton<LogSink>();
        services.AddSingleton(typeof(ILogger<>), typeof(RecordingLogger<>));
        services.AddSingleton<IMeterFactory, TestMeterFactory>();
        services.AddSingleton<RequestMetrics>();

        services.AddScoped<PipelineLog>();
        services.AddScoped<ScopeMarker>();

        // Behaviours last and by hand, because registration order is pipeline
        // order (§6.3) and each test declares the pipeline it is about.
        behaviours?.Invoke(services);

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }
}
