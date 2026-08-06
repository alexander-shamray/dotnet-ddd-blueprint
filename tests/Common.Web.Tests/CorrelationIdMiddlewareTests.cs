using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Xunit;

namespace Common.Web.Tests;

public class CorrelationIdMiddlewareTests
{
    private const string Header = "X-Correlation-Id";

    [Fact]
    public async Task A_request_without_the_header_is_assigned_an_id()
    {
        using IHost host = await TestPipeline.StartAsync(_ => Task.CompletedTask);
        using HttpClient client = host.GetTestClient();

        HttpResponseMessage response = await client.GetAsync("/", TestContext.Current.CancellationToken);

        CorrelationIdOf(response).ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task A_supplied_id_is_kept()
    {
        using IHost host = await TestPipeline.StartAsync(_ => Task.CompletedTask);
        using HttpClient client = host.GetTestClient();
        client.DefaultRequestHeaders.Add(Header, "from-the-gateway");

        HttpResponseMessage response = await client.GetAsync("/", TestContext.Current.CancellationToken);

        CorrelationIdOf(response).ShouldBe("from-the-gateway");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_blank_id_is_replaced_rather_than_propagated(string supplied)
    {
        using IHost host = await TestPipeline.StartAsync(_ => Task.CompletedTask);
        using HttpClient client = host.GetTestClient();
        client.DefaultRequestHeaders.Add(Header, supplied);

        HttpResponseMessage response = await client.GetAsync("/", TestContext.Current.CancellationToken);

        // FirstOrDefault on an absent header is null, but an empty value is
        // not — and would otherwise become a correlation ID of "" (§10.4).
        CorrelationIdOf(response).ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task The_id_is_readable_by_the_middleware_below()
    {
        // Not decoration: UseExceptionHandler sits above this middleware, so an
        // unwinding request has lost the log scope by the time §10.5 builds the
        // response. Request.Headers is what survives, and CustomizeProblemDetails
        // reads it from there.
        using IHost host = await TestPipeline.StartAsync(
            async context => await context.Response.WriteAsync(context.Request.Headers[Header]!));
        using HttpClient client = host.GetTestClient();

        HttpResponseMessage response = await client.GetAsync("/", TestContext.Current.CancellationToken);

        string seenBelow = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        seenBelow.ShouldBe(CorrelationIdOf(response));
    }

    [Fact]
    public async Task The_current_trace_supplies_the_id_when_the_client_does_not()
    {
        using Activity activity = new Activity("incoming").Start();
        using IHost host = await TestPipeline.StartAsync(_ => Task.CompletedTask);
        using HttpClient client = host.GetTestClient();

        HttpResponseMessage response = await client.GetAsync("/", TestContext.Current.CancellationToken);

        // One request, one ID, in both the log pipeline and the trace backend.
        CorrelationIdOf(response).ShouldBe(activity.TraceId.ToString());
    }

    [Fact]
    public async Task A_request_with_no_trace_is_assigned_a_new_identifier()
    {
        Activity.Current = null;
        using IHost host = await TestPipeline.StartAsync(_ => Task.CompletedTask);
        using HttpClient client = host.GetTestClient();

        HttpResponseMessage response = await client.GetAsync("/", TestContext.Current.CancellationToken);

        // "D" format, with the dashes: a TraceId is 32 undashed hex characters
        // and parses as a GUID under "N", so only the exact format tells the
        // two branches of §10.4's fallback apart.
        Guid.TryParseExact(CorrelationIdOf(response), "D", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task The_id_is_pushed_onto_the_log_scope()
    {
        RecordingLoggerProvider logs = new();
        using IHost host = await TestPipeline.StartAsync(_ => Task.CompletedTask, logs);
        using HttpClient client = host.GetTestClient();

        HttpResponseMessage response = await client.GetAsync("/", TestContext.Current.CancellationToken);

        IReadOnlyDictionary<string, object> scope = logs.Scopes
            .OfType<IReadOnlyDictionary<string, object>>()
            .ShouldHaveSingleItem();
        scope["CorrelationId"].ShouldBe(CorrelationIdOf(response));
    }

    private static string? CorrelationIdOf(HttpResponseMessage response) =>
        response.Headers.TryGetValues(Header, out IEnumerable<string>? values) ? values.Single() : null;
}
