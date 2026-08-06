namespace Common.Application;

/// <summary>
/// The entry point for commands and queries. MediatR is the conventional
/// choice and moved to a commercial licence in 2025; the functionality needed
/// here is the eighty lines behind this interface, and writing them removes a
/// licence obligation, a dependency, and a layer of reflection-driven
/// indirection that makes stack traces harder to read (ADR-004).
/// </summary>
public interface IDispatcher
{
    Task<TResult> SendAsync<TResult>(ICommand<TResult> command, CancellationToken ct = default);

    Task<TResult> QueryAsync<TResult>(IQuery<TResult> query, CancellationToken ct = default);
}
