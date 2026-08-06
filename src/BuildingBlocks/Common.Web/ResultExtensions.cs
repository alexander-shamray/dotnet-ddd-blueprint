using Common.Application;
using Microsoft.AspNetCore.Http;

namespace Common.Web;

/// <summary>
/// The one place §10.5's status-code table is executed rather than remembered.
/// An endpoint returns <c>result.ToHttpResult()</c> and decides nothing.
/// </summary>
public static class ResultExtensions
{
    /// <summary>
    /// 204 on success — a command that returns nothing has no body to send —
    /// and the problem response its <see cref="Error"/> selects otherwise.
    /// </summary>
    public static IResult ToHttpResult(this Result result) =>
        result.IsSuccess ? Results.NoContent() : Problem(result.Error);

    /// <summary>
    /// 200 carrying the value, and the same problem response on failure.
    /// </summary>
    /// <remarks>
    /// <see cref="Result{TValue}"/> derives from <see cref="Result"/>, so both
    /// overloads are applicable to it and only the identity conversion makes
    /// this one win. A value result held in a <see cref="Result"/>-typed local
    /// therefore takes the overload above and 204s its payload away — silently,
    /// since no status code can report a body that was never asked for.
    /// </remarks>
    public static IResult ToHttpResult<TValue>(this Result<TValue> result) =>
        result.IsSuccess ? Results.Ok(result.Value) : Problem(result.Error);

    private static IResult Problem(Error error) =>
        Results.Problem(
            detail: error.Description,
            statusCode: StatusFor(error.Type),
            extensions: new Dictionary<string, object?> { ["code"] = error.Code });

    // Title is left to the framework, which fills in the status phrase RFC 9457
    // already defines. Code goes into an extension member instead: it is the
    // stable identifier a client switches on, and a title carrying this
    // service's vocabulary would make every client parse prose to find it.
    private static int StatusFor(ErrorType type) => type switch
    {
        ErrorType.NotFound => StatusCodes.Status404NotFound,
        ErrorType.Rule => StatusCodes.Status422UnprocessableEntity,
        ErrorType.Unavailable => StatusCodes.Status503ServiceUnavailable,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "No status is mapped for this type.")
    };
}
