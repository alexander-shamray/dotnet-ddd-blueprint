using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Common.Web.Tests;

/// <summary>
/// A host running the real middleware pipeline over an in-memory transport.
/// The three pieces this assembly covers only fail together: a ProblemDetails
/// customiser that never runs on what <c>ToHttpResult</c> writes is invisible
/// to any test that inspects the <see cref="IResult"/> alone (§10.4, §10.5).
/// </summary>
internal static class TestPipeline
{
    internal static Task<IHost> StartAsync(RequestDelegate terminal, ILoggerProvider? logs = null) =>
        new HostBuilder()
            .ConfigureWebHost(web =>
            {
                // The correlation middleware reads Activity.Current, which is
                // an AsyncLocal. Without this the test's activity does not
                // reach the request and the fallback branch is untestable.
                web.UseTestServer(options => options.PreserveExecutionContext = true);

                web.ConfigureServices(services => services.AddCommonProblemDetails());

                web.Configure(app =>
                {
                    app.UseCorrelationId();
                    app.Run(terminal);
                });
            })
            .ConfigureLogging(logging =>
                logging.ClearProviders().AddProvider(logs ?? NullLoggerProvider.Instance))
            .StartAsync();
}
