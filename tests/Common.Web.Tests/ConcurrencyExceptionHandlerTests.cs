using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace Common.Web.Tests;

/// <summary>
/// §10.5's 409 row, on the wire, built the same way
/// <c>ValidationExceptionHandlerTests</c> builds the 400's: the §4.2 pipeline
/// shape with the exception handler outermost, rather than
/// <see cref="TestPipeline"/>, which deliberately has no exception handler.
/// </summary>
/// <remarks>
/// §7.3 promised this translation from the beginning and nothing performed it
/// until PR-18. The gap was invisible for as long as it was: a conflict needs
/// a mapped <c>rowversion</c> on an aggregate a request can mutate, and
/// Ordering's <c>Order</c> is the first of those in the solution — Catalog
/// maps none, so no test and no running host could have produced one.
/// </remarks>
public class ConcurrencyExceptionHandlerTests
{
    [Fact]
    public async Task A_concurrency_exception_becomes_a_409()
    {
        using IHost host = await StartThrowingAsync(new DbUpdateConcurrencyException("stale"));
        using HttpClient client = host.GetTestClient();

        HttpResponseMessage response = await client.GetAsync("/orders/cancel", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
    }

    [Fact]
    public async Task The_409_carries_the_same_customisation_as_every_other_problem_response()
    {
        using IHost host = await StartThrowingAsync(new DbUpdateConcurrencyException("stale"));
        using HttpClient client = host.GetTestClient();

        HttpResponseMessage response = await client.GetAsync("/orders/cancel", TestContext.Current.CancellationToken);

        // The status assertion is not redundant with the test above, and
        // leaving it out made this test vacuous — observed, not supposed. The
        // 500 fallback writes through the same IProblemDetailsService, so it
        // carries the same instance, traceId and correlationId; with the
        // handler unregistered every assertion below still passed against the
        // 500. A test about the 409's body has to establish that the response
        // is the 409.
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        using JsonDocument body = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        // §10.5 opens by promising one error shape regardless of which service
        // produced it, and a status written around IProblemDetailsService
        // loses all three of these silently — which is how 401 and 403 broke
        // the same promise for two releases (§10.5's own table says so).
        body.RootElement.GetProperty("instance").GetString().ShouldBe("GET /orders/cancel");
        body.RootElement.TryGetProperty("traceId", out _).ShouldBeTrue();
        body.RootElement.TryGetProperty("correlationId", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task The_detail_names_no_entity_and_no_row_version()
    {
        using IHost host = await StartThrowingAsync(new DbUpdateConcurrencyException("Order 42 had RowVersion 0x0B"));
        using HttpClient client = host.GetTestClient();

        HttpResponseMessage response = await client.GetAsync("/orders/cancel", TestContext.Current.CancellationToken);

        using JsonDocument body = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        // The framework's message names the entity and its version, both of
        // which are storage details (§7.3). Echoing it would put the schema on
        // the wire for a status whose whole content is "re-read and retry".
        string detail = body.RootElement.GetProperty("detail").GetString()!;
        detail.ShouldNotContain("RowVersion");
        detail.ShouldNotContain("42");
    }

    [Fact]
    public async Task A_client_that_cannot_accept_problem_json_still_gets_the_409()
    {
        // TryWriteAsync declines when content negotiation fails, and echoing
        // that false would report the exception unhandled — turning a race the
        // client can retry into a 500 over a request header.
        using IHost host = await StartThrowingAsync(new DbUpdateConcurrencyException("stale"));
        using HttpClient client = host.GetTestClient();
        client.DefaultRequestHeaders.Accept.ParseAdd("application/xml");

        HttpResponseMessage response = await client.GetAsync("/orders/cancel", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task A_plain_update_exception_is_not_a_conflict()
    {
        // DbUpdateException is the base type and covers a violated constraint,
        // which is not a race and must not tell the client to retry — the
        // second attempt would fail identically. Pattern-matching the derived
        // type is what keeps them apart, and `is DbUpdateException` would not.
        using IHost host = await StartThrowingAsync(new DbUpdateException("constraint"));
        using HttpClient client = host.GetTestClient();

        HttpResponseMessage response = await client.GetAsync("/orders/cancel", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
    }

    [Fact]
    public async Task Any_other_exception_still_falls_through_to_the_500()
    {
        using IHost host = await StartThrowingAsync(new InvalidOperationException("boom"));
        using HttpClient client = host.GetTestClient();

        HttpResponseMessage response = await client.GetAsync("/orders/cancel", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
    }

    private static Task<IHost> StartThrowingAsync(Exception exception) =>
        new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services => services.AddCommonProblemDetails());
                web.Configure(app =>
                {
                    app.UseExceptionHandler();
                    app.Run(_ => throw exception);
                });
            })
            .ConfigureLogging(logging => logging.ClearProviders())
            .StartAsync();
}
