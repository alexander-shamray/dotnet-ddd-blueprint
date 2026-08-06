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
    /// Registers the shared problem-details customisation. Composed by
    /// <c>AddCommonWebDefaults</c> (§13.2) rather than called directly by a
    /// host — it is one of the things every host needs identically.
    /// </summary>
    public static IServiceCollection AddCommonProblemDetails(this IServiceCollection services) =>
        services.AddProblemDetails(options =>
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
