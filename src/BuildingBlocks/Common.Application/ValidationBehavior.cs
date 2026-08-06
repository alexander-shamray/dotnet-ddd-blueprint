using FluentValidation;
using FluentValidation.Results;

namespace Common.Application;

/// <summary>
/// Runs every registered validator for the request and fails fast, before any
/// I/O. Unconstrained on purpose: a malformed query is as worth rejecting as a
/// malformed command, and the two behaviours below it in the pipeline are the
/// ones that belong to the write path alone (§6.3).
/// </summary>
public sealed class ValidationBehavior<TRequest, TResult>(IEnumerable<IValidator<TRequest>> validators)
    : IPipelineBehavior<TRequest, TResult>
{
    public async Task<TResult> HandleAsync(TRequest request, NextDelegate<TResult> next, CancellationToken ct)
    {
        if (!validators.Any())
            return await next();

        // One context per validator, not one shared between them. A
        // ValidationContext<T> accumulates failures into a list that every
        // ValidationResult built from it then reports as its own, so two
        // validators over one context each come back carrying both failures
        // and the caller sees every problem twice. The concurrent adds to that
        // list are a race besides — Task.WhenAll runs the validators together.
        ValidationResult[] results = await Task.WhenAll(
            validators.Select(v => v.ValidateAsync(new ValidationContext<TRequest>(request), ct)));

        ValidationFailure[] failures = [.. results.SelectMany(r => r.Errors).Where(f => f is not null)];

        if (failures.Length > 0)
            throw new ValidationException(failures);

        return await next();
    }
}
