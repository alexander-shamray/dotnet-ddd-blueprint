using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Common.Web;

/// <summary>
/// The three signals of §13.1, configured once here and referenced by every
/// service host (§13.2). Composed by <c>AddCommonWebDefaults</c> rather than
/// called directly by a host.
/// </summary>
public static class ObservabilityExtensions
{
    /// <summary>
    /// Registers the logging pipeline, metrics, tracing and OTLP export.
    /// </summary>
    public static IHostApplicationBuilder AddObservability(this IHostApplicationBuilder builder)
    {
        string serviceName = builder.Environment.ApplicationName;

        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;

            // §13.4's "never log a secret" rule, given a mechanism. Registered
            // here because this is the only logging pipeline the host has — a
            // redaction policy configured on a library nobody installed
            // redacts nothing, and reads in review as though it does.
            logging.AddProcessor(new SensitiveDataRedactor());
        });

        builder.Services
            .AddOpenTelemetry()
            .ConfigureResource(r => r
                .AddService(serviceName, serviceVersion: BuildInfo.Version)
                .AddAttributes([new KeyValuePair<string, object>("deployment.environment", builder.Environment.EnvironmentName)]))
            .WithMetrics(m => m
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                // Every meter an alert or SLO reads from. A condition whose
                // signal is not registered here cannot fire — it looks
                // configured and is silent, which is worse than having no
                // alert at all. Registered ahead of the instruments: a name is
                // a string and costs nothing, and the alternative is spreading
                // one block's edits across six later pull requests.
                .AddMeter("Ordering.Orders")                       // §13.3, §13.6
                .AddMeter("Ordering.Outbox")                       // §13.6 per-lane

                // Shared names, not service-prefixed: every service emits the
                // same instruments and the service.name resource attribute
                // separates them. One dashboard query then works for all of
                // them, and a new service appears on it without anyone editing
                // a panel.
                .AddMeter("Commerce.Requests")                     // §13.3, §13.7
                .AddMeter("Commerce.Messaging")                    // §13.3, §13.7
                .AddMeter("MassTransit")
                .AddMeter("Microsoft.Extensions.Caching.Hybrid")   // cache hit ratio
                .AddMeter("StackExchange.Redis"))
            .WithTracing(t => t
                .AddAspNetCoreInstrumentation(o =>
                    o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health"))
                .AddHttpClientInstrumentation()
                // AddEntityFrameworkCoreInstrumentation and
                // AddRedisInstrumentation land with the packages they
                // instrument, at PR-08 and PR-12. Unlike a meter name, each
                // costs a package reference — a claim about the dependency
                // graph that nothing here would yet make true.
                .AddSource("MassTransit"))
            .UseOtlpExporter();

        return builder;
    }
}
