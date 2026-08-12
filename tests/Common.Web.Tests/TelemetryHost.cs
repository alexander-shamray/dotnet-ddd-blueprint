using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Common.Web.Tests;

/// <summary>
/// A host builder shaped like a service host, for asserting what
/// <c>AddObservability</c> registers.
/// </summary>
internal static class TelemetryHost
{
    internal const string ServiceName = "Probe.Service";
    internal const string EnvironmentName = "Testing";
    internal const string Authority = "https://identity.invalid/realms/test";

    /// <param name="environmentName">
    /// Overrides <see cref="EnvironmentName"/>. Only the JWT tests pass one,
    /// because <c>RequireHttpsMetadata</c> is the only thing in
    /// <c>AddCommonWebDefaults</c> whose <em>behaviour</em> turns on it —
    /// Development alone allows metadata over plain HTTP (§11.3), so a test
    /// asserting either side of that has to say which side it is on.
    /// <c>AddObservability</c> also reads the environment, as
    /// <c>deployment.environment</c> on the resource (§13.2), so overriding
    /// this changes that attribute too. Nothing asserts both at once today, and
    /// a test that did would have to pass the same name to both expectations.
    /// </param>
    internal static HostApplicationBuilder Builder(string? environmentName = null)
    {
        HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(
            new HostApplicationBuilderSettings
            {
                ApplicationName = ServiceName,
                EnvironmentName = environmentName ?? EnvironmentName
            });

        // AddObservability calls UseOtlpExporter, which blocks for its full
        // ten-second timeout trying to reach a collector no test runs. 200 ms
        // takes a host from 8.3 s to 0.44 s — both measured. The exporter stays
        // wired and simply gives up quickly, which is the point: omitting it
        // would stop testing the line the PR exists to add.
        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["OTEL_EXPORTER_OTLP_TIMEOUT"] = "200",

                // AddCommonWebDefaults reads this key eagerly and throws
                // naming it (§11.3), so every host built here must supply one.
                // Unreachable on purpose: .invalid never resolves, so a test
                // that dials the authority fails loudly rather than reaching a
                // real identity provider. The one test that wants the throw
                // builds its own builder without this line.
                [AuthenticationExtensions.AuthorityKey] = Authority
            });

        // CreateEmptyApplicationBuilder registers nothing. IMeterFactory is
        // what the meters below are created through.
        builder.Services.AddMetrics();

        return builder;
    }
}
