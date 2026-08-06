namespace Common.Application;

/// <summary>
/// The rest of the pipeline, as seen from one behaviour. Calling it runs
/// everything nested inside; not calling it short-circuits the request.
/// </summary>
public delegate Task<TResult> NextDelegate<TResult>();

/// <summary>
/// A cross-cutting concern wrapped around a request. Behaviours nest
/// outermost-first in registration order (§6.3), and are the one pluggable
/// interface deliberately left out of the §6.2 convention scan — a scan
/// registers types in whatever order reflection returns them, which is
/// unspecified, and here the order is the design.
/// </summary>
public interface IPipelineBehavior<in TRequest, TResult>
{
    Task<TResult> HandleAsync(TRequest request, NextDelegate<TResult> next, CancellationToken ct);
}
