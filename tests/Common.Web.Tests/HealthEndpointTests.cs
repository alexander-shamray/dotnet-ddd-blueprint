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

    // ownsNoDependencies defaults to FALSE here, matching the production
    // default, so a test that registers no readiness check has to say so —
    // which is what makes the three that pass it evidence rather than
    // configuration.
    private static Task<IHost> StartAsync(
        Action<IHealthChecksBuilder> checks,
        bool ownsNoDependencies = false) =>
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
                    app.UseEndpoints(endpoints => endpoints.MapCommonHealthEndpoints(ownsNoDependencies));
                });
            })
            .ConfigureLogging(logging => logging.ClearProviders())
            .StartAsync(TestContext.Current.CancellationToken);

    private static Task<HttpResponseMessage> GetAsync(IHost host, string path) =>
        host.GetTestClient().GetAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);

    [Fact]
    public async Task Liveness_passes_with_no_checks_registered()
    {
        using IHost host = await StartAsync(_ => { }, ownsNoDependencies: true);

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
        using IHost host = await StartAsync(
            checks => checks.AddCheck("outbox", new Always(HealthStatus.Unhealthy), tags: ["observe"]),
            ownsNoDependencies: true);

        HttpResponseMessage ready = await GetAsync(host, "/health/ready");

        ready.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Startup_gates_on_the_ready_tagged_checks()
    {
        // Asserted by request, because the route string is the contract. The
        // kubelet's startupProbe holds its own copy of "/health/startup" in a
        // manifest no compiler reads, so nothing here links the two: change
        // the route to "/health/startupp" and every other test in this file
        // stays green while a slow-starting pod 404s and is killed mid-boot.
        //
        // Counting endpoints does not close that gap — three endpoints exist
        // under any spelling. Only a GET does.
        using (IHost healthy = await StartAsync(checks =>
            checks.AddCheck("sql", new Always(HealthStatus.Healthy), tags: ["ready"])))
        {
            HttpResponseMessage startup = await GetAsync(healthy, "/health/startup");

            startup.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        // The control: 200 above could equally mean the predicate matched
        // nothing at all, since an empty predicate set is a passing one.
        using IHost failing = await StartAsync(checks =>
            checks.AddCheck("sql", new Always(HealthStatus.Unhealthy), tags: ["ready"]));

        HttpResponseMessage unavailable = await GetAsync(failing, "/health/startup");

        unavailable.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task Every_probe_allows_anonymous()
    {
        // Asserted on the metadata rather than by an unauthenticated request.
        // This host maps no endpoint behind a policy, so an unauthenticated
        // request would answer 200 whether or not the probes were anonymous —
        // it would pass for a reason that has nothing to do with the claim.
        // §13.5's rule is about the metadata anyway: a probe that 401s is read
        // by the kubelet as unhealthy and the pod is killed in a loop.
        using IHost host = await StartAsync(_ => { }, ownsNoDependencies: true);

        IReadOnlyList<Endpoint> endpoints = host.Services
            .GetRequiredService<EndpointDataSource>()
            .Endpoints;

        endpoints.Count.ShouldBe(3);
        foreach (Endpoint endpoint in endpoints)
            endpoint.Metadata.GetMetadata<IAllowAnonymous>().ShouldNotBeNull(endpoint.DisplayName);
    }

    [Fact]
    public async Task A_host_with_no_readiness_check_refuses_to_start()
    {
        // The whole of §13.5's fail-open, and the reason it is worth a startup
        // failure: an empty predicate set is a passing predicate set, so this
        // host would otherwise answer /health/ready with 200 while reaching
        // nothing — and §15.1 removes the smoke stage by name on the grounds
        // that this probe already gates the rollout.
        InvalidOperationException thrown = await Should.ThrowAsync<InvalidOperationException>(
            () => StartAsync(_ => { }));

        thrown.Message.ShouldContain("ready");
    }

    [Fact]
    public async Task An_observe_tagged_check_does_not_satisfy_the_guard()
    {
        // The control that stops the guard passing on any registration at all.
        // A host holding only an observe-tagged check has nothing the readiness
        // predicate will select, so it is the empty case wearing a health
        // check's clothes — which is exactly the shape a refactor produces
        // when it retags rather than deletes.
        await Should.ThrowAsync<InvalidOperationException>(
            () => StartAsync(checks =>
                checks.AddCheck("outbox", new Always(HealthStatus.Healthy), tags: ["observe"])));
    }

    [Fact]
    public async Task A_host_that_declares_it_owns_nothing_starts_and_reports_ready()
    {
        // The gateway and the BFF, which is the case the parameter exists for.
        // Paired with the two above so the guard is shown refusing and
        // admitting: a guard only ever observed one way is one nobody has
        // established is looking at anything.
        using IHost host = await StartAsync(_ => { }, ownsNoDependencies: true);

        HttpResponseMessage ready = await GetAsync(host, "/health/ready");

        ready.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
