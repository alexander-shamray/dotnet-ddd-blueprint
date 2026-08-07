using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace Common.Web.Tests;

public class HealthEndpointTests
{
    private sealed class Always(HealthStatus status) : IHealthCheck
    {
        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct) =>
            Task.FromResult(new HealthCheckResult(status));
    }

    private static Task<IHost> StartAsync(Action<IHealthChecksBuilder> checks) =>
        new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    checks(services.AddHealthChecks());
                    services.AddRouting();
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints => endpoints.MapCommonHealthEndpoints());
                });
            })
            .ConfigureLogging(logging => logging.ClearProviders())
            .StartAsync(TestContext.Current.CancellationToken);

    private static Task<HttpResponseMessage> GetAsync(IHost host, string path) =>
        host.GetTestClient().GetAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);

    [Fact]
    public async Task Liveness_passes_with_no_checks_registered()
    {
        using IHost host = await StartAsync(_ => { });

        HttpResponseMessage response = await GetAsync(host, "/health/live");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Liveness_ignores_a_failing_dependency_that_readiness_reports()
    {
        // §13.5's rule, and the one whose failure mode is a restart storm: if
        // liveness checked the database, a brief outage would restart every pod
        // simultaneously and the storm would outlast the outage.
        using IHost host = await StartAsync(checks =>
            checks.AddCheck("sql", new Always(HealthStatus.Unhealthy), tags: ["ready"]));

        HttpResponseMessage live = await GetAsync(host, "/health/live");
        HttpResponseMessage ready = await GetAsync(host, "/health/ready");

        live.StatusCode.ShouldBe(HttpStatusCode.OK);
        ready.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task Readiness_passes_when_its_checks_pass()
    {
        using IHost host = await StartAsync(checks =>
            checks.AddCheck("sql", new Always(HealthStatus.Healthy), tags: ["ready"]));

        HttpResponseMessage ready = await GetAsync(host, "/health/ready");

        ready.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Readiness_ignores_an_observe_tagged_check()
    {
        // The outbox is tagged observe, scraped and alerted on (§13.6), and
        // deliberately not part of any probe: gating readiness on a backlog
        // turns a delivery delay into a total outage.
        using IHost host = await StartAsync(checks =>
            checks.AddCheck("outbox", new Always(HealthStatus.Unhealthy), tags: ["observe"]));

        HttpResponseMessage ready = await GetAsync(host, "/health/ready");

        ready.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Every_probe_allows_anonymous()
    {
        // Asserted on the metadata rather than by an unauthenticated request:
        // there is no authentication scheme to be anonymous against until
        // PR-16, and a request would pass for the wrong reason today and stop
        // meaning anything the moment one exists.
        using IHost host = await StartAsync(_ => { });

        IReadOnlyList<Endpoint> endpoints = host.Services
            .GetRequiredService<EndpointDataSource>()
            .Endpoints;

        endpoints.Count.ShouldBe(3);
        foreach (Endpoint endpoint in endpoints)
            endpoint.Metadata.GetMetadata<IAllowAnonymous>().ShouldNotBeNull(endpoint.DisplayName);
    }
}
