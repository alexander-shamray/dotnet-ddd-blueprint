using Microsoft.Extensions.Logging;

namespace Common.Application;

/// <summary>
/// Registered first, so it is outermost (§6.3): the span covers validation,
/// idempotency, the transaction and the handler.
/// </summary>
/// <remarks>
/// <c>outcome</c> is <c>ok</c> or <c>error</c>, and a returned failure is
/// <c>ok</c>. The behaviour is generic over <typeparamref name="TResult"/> and
/// cannot see inside it without a constraint that would exclude queries — but
/// the deeper reason is that a rejected command is a normal outcome of a
/// working system, and counting it as an error makes the one number that
/// should mean "something is broken" track customer behaviour instead (§13.3).
/// </remarks>
public sealed class LoggingBehavior<TRequest, TResult>(
    ILogger<LoggingBehavior<TRequest, TResult>> logger,
    RequestMetrics metrics,
    TimeProvider clock)
    : IPipelineBehavior<TRequest, TResult>
{
    // Compiled once per closed behaviour rather than parsed per request. CA1848
    // is met rather than waived here: this behaviour is outermost on every
    // dispatched request, which is exactly the hot path the rule is about.
    private static readonly Action<ILogger, string, double, Exception?> Completed =
        LoggerMessage.Define<string, double>(
            LogLevel.Information,
            new EventId(1, nameof(Completed)),
            "{RequestType} completed in {ElapsedMs} ms");

    private static readonly Action<ILogger, string, Exception?> Threw =
        LoggerMessage.Define<string>(
            LogLevel.Error,
            new EventId(2, nameof(Threw)),
            "{RequestType} threw");

    public async Task<TResult> HandleAsync(TRequest request, NextDelegate<TResult> next, CancellationToken ct)
    {
        string name = typeof(TRequest).Name;
        long start = clock.GetTimestamp();

        // A scope, not a log property: everything written inside the handler
        // inherits it, including EF Core's and MassTransit's own logging.
        using IDisposable? scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["RequestType"] = name
        });

        try
        {
            TResult result = await next();

            // Read once and used twice. Two calls to GetElapsedTime would put
            // a different number in the log line and the histogram, and the
            // one a reader trusts is whichever they looked at first.
            TimeSpan elapsed = clock.GetElapsedTime(start);

            Completed(logger, name, elapsed.TotalMilliseconds, null);
            metrics.Recorded(name, "ok", elapsed);

            return result;
        }
        catch (Exception ex)
        {
            Threw(logger, name, ex);
            metrics.Recorded(name, "error", clock.GetElapsedTime(start));
            throw;
        }
    }
}
