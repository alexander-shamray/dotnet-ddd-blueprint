using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;

namespace Common.Application;

/// <summary>
/// Resolves the handler for a request, wraps it in the registered behaviours
/// and runs the result. One invoker instance is cached per concrete request
/// type, so the reflection cost is paid once per type rather than once per
/// call (§6.2).
/// </summary>
/// <remarks>
/// Internal: a service registers it through <c>AddDispatcher</c> and depends on
/// <see cref="IDispatcher"/>. The cache is static and outlives every scope,
/// which is safe precisely because an invoker holds no state — the provider is
/// handed to it per call.
/// </remarks>
internal sealed class Dispatcher(IServiceProvider services) : IDispatcher
{
    private static readonly ConcurrentDictionary<Type, object> Invokers = new();

    public Task<TResult> SendAsync<TResult>(ICommand<TResult> command, CancellationToken ct = default) =>
        GetInvoker<TResult>(command.GetType(), typeof(CommandInvoker<,>))
            .InvokeAsync(services, command, ct);

    public Task<TResult> QueryAsync<TResult>(IQuery<TResult> query, CancellationToken ct = default) =>
        GetInvoker<TResult>(query.GetType(), typeof(QueryInvoker<,>))
            .InvokeAsync(services, query, ct);

    private static Invoker<TResult> GetInvoker<TResult>(Type requestType, Type openInvoker) =>
        (Invoker<TResult>)Invokers.GetOrAdd(
            requestType,
            _ => Activator.CreateInstance(openInvoker.MakeGenericType(requestType, typeof(TResult)))!);

    private abstract class Invoker<TResult>
    {
        public abstract Task<TResult> InvokeAsync(IServiceProvider services, object request, CancellationToken ct);
    }

    private sealed class CommandInvoker<TCommand, TResult> : Invoker<TResult>
        where TCommand : ICommand<TResult>
    {
        public override Task<TResult> InvokeAsync(IServiceProvider services, object request, CancellationToken ct)
        {
            TCommand typed = (TCommand)request;
            ICommandHandler<TCommand, TResult> handler =
                services.GetRequiredService<ICommandHandler<TCommand, TResult>>();

            NextDelegate<TResult> pipeline = () => handler.HandleAsync(typed, ct);

            // Reversed so the first-registered behaviour is the outermost.
            foreach (IPipelineBehavior<TCommand, TResult> behavior in services
                .GetServices<IPipelineBehavior<TCommand, TResult>>()
                .Reverse())
            {
                NextDelegate<TResult> next = pipeline;
                pipeline = () => behavior.HandleAsync(typed, next, ct);
            }

            return pipeline();
        }
    }

    private sealed class QueryInvoker<TQuery, TResult> : Invoker<TResult>
        where TQuery : IQuery<TResult>
    {
        public override Task<TResult> InvokeAsync(IServiceProvider services, object request, CancellationToken ct)
        {
            TQuery typed = (TQuery)request;
            IQueryHandler<TQuery, TResult> handler =
                services.GetRequiredService<IQueryHandler<TQuery, TResult>>();

            NextDelegate<TResult> pipeline = () => handler.HandleAsync(typed, ct);

            foreach (IPipelineBehavior<TQuery, TResult> behavior in services
                .GetServices<IPipelineBehavior<TQuery, TResult>>()
                .Reverse())
            {
                NextDelegate<TResult> next = pipeline;
                pipeline = () => behavior.HandleAsync(typed, next, ct);
            }

            return pipeline();
        }
    }
}
