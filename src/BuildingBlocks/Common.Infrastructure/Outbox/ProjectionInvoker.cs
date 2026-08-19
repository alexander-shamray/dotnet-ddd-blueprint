using System.Collections.Concurrent;
using Common.Application;
using Common.Infrastructure.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace Common.Infrastructure.Outbox;

/// <summary>
/// Resolves and calls the projection handlers for a runtime type. The same
/// cached-delegate approach as §6.2's dispatcher, so the reflection cost is
/// paid once per event type rather than once per message.
/// </summary>
internal static class ProjectionInvoker
{
    private static readonly ConcurrentDictionary<Type, Invoker> Cache = new();

    public static Task InvokeAllAsync(
        IServiceProvider sp,
        object payload,
        Type eventType,
        DateTimeOffset occurredAt,
        CancellationToken ct) =>
        Cache
            .GetOrAdd(eventType, static t => (Invoker)Activator.CreateInstance(typeof(Invoker<>).MakeGenericType(t))!)
            .InvokeAllAsync(sp, payload, occurredAt, ct);

    private abstract class Invoker
    {
        public abstract Task InvokeAllAsync(
            IServiceProvider sp,
            object payload,
            DateTimeOffset occurredAt,
            CancellationToken ct);
    }

    private sealed class Invoker<TEvent> : Invoker
    {
        public override async Task InvokeAllAsync(
            IServiceProvider sp,
            object payload,
            DateTimeOffset occurredAt,
            CancellationToken ct)
        {
            IProjectionHandler<TEvent>[] handlers = [.. sp.GetServices<IProjectionHandler<TEvent>>()];

            // A Local row is staged only when IProjectionRegistry found a
            // handler (§7.5). Finding none here means the handler was
            // implemented but never registered — fail loudly rather than
            // marking the row processed having done nothing.
            if (handlers.Length == 0)
            {
                throw new InvalidOperationException(
                    $"No IProjectionHandler<{typeof(TEvent).Name}> is registered, " +
                    "but a Local outbox row was staged for it. Check the §6.2 scan.");
            }

            // Sequential, not concurrent: two projections writing the same read
            // table in parallel is a deadlock waiting for load to find it.
            foreach (IProjectionHandler<TEvent> handler in handlers)
                await handler.HandleAsync((TEvent)payload, ct);

            // Raised-to-applied (§13.7), recorded after the handlers rather
            // than before: the SLO is about when the read model became
            // correct, not when work on it started. Resolved from sp because
            // this type is static and cached — it has no constructor to inject.
            sp.GetRequiredService<MessagingMetrics>().Projected(
                typeof(TEvent).Name,
                sp.GetRequiredService<TimeProvider>().GetUtcNow() - occurredAt);
        }
    }
}
