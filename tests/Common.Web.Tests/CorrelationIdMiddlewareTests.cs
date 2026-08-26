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

    [Theory]
    [InlineData("has spaces")]
    [InlineData("semi;colon")]
    [InlineData("angle<bracket>")]
    [InlineData("quote\"mark")]
    [InlineData("percent%2f")]
    public async Task An_implausible_id_is_replaced_rather_than_echoed(string supplied)
    {
        // The middleware runs above UseAuthentication (§4.2), so this input is
        // unauthenticated on every request that reaches a host — and the
        // adopted value is reflected in the response header, in §10.5's problem
        // body, and on the log scope every record for the request inherits.
        // Refused, not sanitised: a rejected value must not reach any of them.
        string? echoed = await EchoedFor(supplied);

        echoed.ShouldNotBe(supplied);
        echoed.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task An_over_long_id_is_replaced()
    {
        // The bound is on the platform's cost rather than on the caller's
        // taste: Kestrel's header budget is tens of kilobytes, and that string
        // would be attached to a scope inherited by every record the request
        // produces — EF Core's and MassTransit's included — and echoed on the
        // response. One request, multiplied by the record count, into collector
        // ingest.
        string supplied = new('a', CorrelationIdExtensions.MaxSuppliedLength + 1);

        (await EchoedFor(supplied)).ShouldNotBe(supplied);
    }

    [Fact]
    public async Task An_id_at_the_bound_is_kept()
    {
        // The control for the test above. Without it "too long is replaced"
        // passes just as well against a middleware that replaces everything —
        // which is what the length check would become if the comparison were
        // written the other way round.
        string supplied = new('a', CorrelationIdExtensions.MaxSuppliedLength);

        (await EchoedFor(supplied)).ShouldBe(supplied);
    }

    [Theory]
    [InlineData("018f4c2e-0000-7000-8000-000000000000")]
    [InlineData("4bf92f3577b34da6a3ce929d0e0e4736")]
    [InlineData("from_the_gateway")]
    public async Task A_plausible_id_is_still_adopted(string supplied)
    {
        // §10.4's promise is that an ID chosen by the caller's own tracing
        // survives the hop, so the alphabet has to admit what other systems
        // mint — a dashed GUID, a 32-hex trace ID, and the underscore an
        // upstream edge commonly uses. Narrowing to exactly this platform's two
        // fallbacks would break the promise to keep the guard tidy.
        (await EchoedFor(supplied)).ShouldBe(supplied);
    }

    // Drives the real pipeline with one supplied header and returns what came
    // back on the response — which is the value the guard either adopted or
    // replaced, and the one a caller can observe.
    private static async Task<string?> EchoedFor(string supplied)
    {
        using IHost host = await TestPipeline.StartAsync(_ => Task.CompletedTask);
        using HttpClient client = host.GetTestClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation(Header, supplied);

        HttpResponseMessage response = await client.GetAsync("/", TestContext.Current.CancellationToken);

        return CorrelationIdOf(response);
    }

    private static string? CorrelationIdOf(HttpResponseMessage response) =>
        response.Headers.TryGetValues(Header, out IEnumerable<string>? values) ? values.Single() : null;
}
