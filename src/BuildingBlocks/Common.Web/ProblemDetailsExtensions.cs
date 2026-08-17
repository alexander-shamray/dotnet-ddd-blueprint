using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Common.Web;

/// <summary>
/// RFC 9457 <c>application/problem+json</c> for every service, so a client
/// handles one error shape regardless of which service produced it (§10.5).
/// </summary>
public static class ProblemDetailsExtensions
{
    /// <summary>
    /// Registers the shared problem-details customisation, and the handler
    /// for §10.5's 400 row — <c>ValidationBehavior</c>'s thrown
    /// <c>ValidationException</c>, which is produced beside the handler and
    /// must be translated beside the other status decisions. Composed by
    /// <c>AddCommonWebDefaults</c> (§13.2) rather than called directly by a
    /// host — it is one of the things every host needs identically.
    /// </summary>
    public static IServiceCollection AddCommonProblemDetails(this IServiceCollection services)
    {
        services.AddExceptionHandler<ValidationExceptionHandler>();

        // §10.5's 409 row, registered beside the 400 for the same reason: both
        // are statuses a mechanism beside the handler produces, so neither is
        // reachable through Error and both need an executor. Order between the
        // two does not matter — each declines every exception but its own —
        // but registration order is the pipeline's, so they are kept adjacent
        // rather than left to be rediscovered separately.
        services.AddExceptionHandler<ConcurrencyExceptionHandler>();

        return services.AddProblemDetails(options =>
            options.CustomizeProblemDetails = context =>
            {
                context.ProblemDetails.Instance =
                    $"{context.HttpContext.Request.Method} {context.HttpContext.Request.Path}";

                // Read from the request rather than the log scope: this is the
                // one path §10.4's middleware keeps alive through an unwinding
                // exception, and an error response is exactly when it matters.
                context.ProblemDetails.Extensions["correlationId"] =
                    context.HttpContext.Request.Headers["X-Correlation-Id"].FirstOrDefault();

                context.ProblemDetails.Extensions["traceId"] =
                    Activity.Current?.Id ?? context.HttpContext.TraceIdentifier;
            });
    }
}
