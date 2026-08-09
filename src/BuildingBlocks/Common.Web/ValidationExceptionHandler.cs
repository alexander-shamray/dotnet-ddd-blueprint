using FluentValidation;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Common.Web;

/// <summary>
/// The 400 row of §10.5's table. <c>ValidationBehavior</c> throws
/// <c>ValidationException</c> before any handler runs (§6.3), and until this
/// type nothing translated it — the pipeline's generic handler answered 500
/// for a malformed request, which is the wrong statement about whose fault it
/// was. Field-keyed <c>errors</c>, because <c>Error</c> has no field: this
/// status is produced by a mechanism beside the handler, never returned by
/// one.
/// </summary>
/// <remarks>
/// Registered by <c>AddCommonProblemDetails</c>; <c>UseExceptionHandler</c>
/// consults registered <c>IExceptionHandler</c>s before its fallback, and this
/// one declines everything but the validation case. Writing through
/// <c>IProblemDetailsService</c> keeps §10.5's customisation on the body, so a
/// 400 carries the same instance, correlation and trace members as every
/// other problem response.
/// </remarks>
internal sealed class ValidationExceptionHandler(IProblemDetailsService problemDetails) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is not ValidationException validation)
            return false;

        Dictionary<string, string[]> errors = validation.Errors
            .GroupBy(f => f.PropertyName, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                string[] (g) => [.. g.Select(f => f.ErrorMessage)],
                StringComparer.Ordinal);

        httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;

        // Handled the moment it matched, whatever the writer negotiates: a
        // client whose Accept header refuses problem+json still sent
        // malformed input, and the status alone must answer it — reporting
        // "unhandled" here would fall through to the 500 fallback and blame
        // the service.
        await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = new ValidationProblemDetails(errors)
            {
                Status = StatusCodes.Status400BadRequest
            }
        });

        return true;
    }
}
