using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;

namespace Common.Application;

/// <summary>
/// Resolves the handler for a request, wraps it in the registered behaviours
/// and runs the result. One invoker instance is cached per concrete request
/// type, result type and kind, so the reflection cost is paid once per
/// combination rather than once per call (§6.2).
/// </summary>
/// <remarks>
/// Internal: a service registers it through <c>AddDispatcher</c> and depends on
/// <see cref="IDispatcher"/>. The cache is static and outlives every scope,
/// which is safe precisely because an invoker holds no state — the provider is
/// handed to it per call.
/// </remarks>
internal sealed class Dispatcher(IServiceProvider services) : IDispatcher
{
    // Keyed on all three parts of what the invoker closes over, because a
    // request type determines neither of the other two. A record may implement
    // ICommand<T> twice under different results, and it may implement both
    // ICommand<T> and IQuery<T> under the same one — and those two collisions
    // fail differently. The first throws an InvalidCastException from inside
    // this class, naming neither the request nor the reason. The second does
    // not throw at all: both invokers derive from Invoker<TResult>, so the cast
    // succeeds and the query quietly runs the command's handler through the
    // command's behaviours.
    private static readonly ConcurrentDictionary<(Type Request, Type Result, Type Kind), object> Invokers = new();

    public Task<TResult> SendAsync<TResult>(ICommand<TResult> command, CancellationToken ct = default) =>
        GetInvoker<TResult>(command.GetType(), typeof(CommandInvoker<,>))
            .InvokeAsync(services, command, ct);

    public Task<TResult> QueryAsync<TResult>(IQuery<TResult> query, CancellationToken ct = default) =>
        GetInvoker<TResult>(query.GetType(), typeof(QueryInvoker<,>))
            .InvokeAsync(services, query, ct);

    private static Invoker<TResult> GetInvoker<TResult>(Type requestType, Type openInvoker) =>
        (Invoker<TResult>)Invokers.GetOrAdd(
            (requestType, typeof(TResult), openInvoker),
            static key => Activator.CreateInstance(key.Kind.MakeGenericType(key.Request, key.Result))!);

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
