using System.Net;
using System.Text.Json;
using Common.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace Common.Web.Tests;

/// <summary>
/// §8.5's contention on the wire, built exactly as
/// <c>ConcurrencyExceptionHandlerTests</c> builds the other 409's.
/// </summary>
/// <remarks>
/// <b>The gap this closes was created by the PR that made the exception
/// reachable.</b> <c>IdempotencyBehavior</c> took its pipeline seat and two
/// HTTP commands opted in, so a duplicate arriving while the first attempt is
/// still running now reaches <c>UseExceptionHandler</c> — which answered 500,
/// because nothing translated it. A client that treats 500 as fatal abandons
/// an operation that was about to succeed, and the mechanism reporting itself
/// as a server fault is the worst available outcome for a feature whose whole
/// purpose is making a retry safe.
/// </remarks>
public class ConcurrentRequestExceptionHandlerTests
{
    private static readonly Guid CommandId = Guid.Parse("6f1d2a70-9c3b-4a1e-8f52-1b7c4d905e33");

    [Fact]
    public async Task A_request_already_in_progress_becomes_a_409()
    {
        using IHost host = await StartThrowingAsync(new ConcurrentRequestException(CommandId));
        using HttpClient client = host.GetTestClient();

        HttpResponseMessage response = await client.GetAsync("/orders", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
    }

    [Fact]
    public async Task The_409_carries_the_same_customisation_as_every_other_problem_response()
    {
        using IHost host = await StartThrowingAsync(new ConcurrentRequestException(CommandId));
        using HttpClient client = host.GetTestClient();

        HttpResponseMessage response = await client.GetAsync("/orders", TestContext.Current.CancellationToken);

        // The status assertion is load-bearing for the reason the sibling suite
        // records: the 500 fallback writes through the same
        // IProblemDetailsService, so every assertion below passes against it
        // with this handler unregistered.
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        using JsonDocument body = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        body.RootElement.GetProperty("instance").GetString().ShouldBe("GET /orders");
        body.RootElement.TryGetProperty("traceId", out _).ShouldBeTrue();
        body.RootElement.TryGetProperty("correlationId", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task The_detail_does_not_echo_the_command_id()
    {
        using IHost host = await StartThrowingAsync(new ConcurrentRequestException(CommandId));
        using HttpClient client = host.GetTestClient();

        HttpResponseMessage response = await client.GetAsync("/orders", TestContext.Current.CancellationToken);

        using JsonDocument body = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        // The framework message names it, and the caller sent it — so echoing
        // it tells them nothing while putting half of a key whose other
        // segment is the subject (§8.5) onto the wire.
        body.RootElement.GetProperty("detail").GetString()!.ShouldNotContain(CommandId.ToString());
    }

    [Fact]
    public async Task A_client_that_cannot_accept_problem_json_still_gets_the_409()
    {
        using IHost host = await StartThrowingAsync(new ConcurrentRequestException(CommandId));
        using HttpClient client = host.GetTestClient();
        client.DefaultRequestHeaders.Accept.ParseAdd("application/xml");

        HttpResponseMessage response = await client.GetAsync("/orders", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Any_other_exception_still_falls_through_to_the_500()
    {
        // The half that establishes the handler is selecting rather than
        // catching: a handler matching everything would pass every assertion
        // above and turn every fault in the platform into a retry instruction.
        using IHost host = await StartThrowingAsync(new InvalidOperationException("boom"));
        using HttpClient client = host.GetTestClient();

        HttpResponseMessage response = await client.GetAsync("/orders", TestContext.Current.CancellationToken);

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
