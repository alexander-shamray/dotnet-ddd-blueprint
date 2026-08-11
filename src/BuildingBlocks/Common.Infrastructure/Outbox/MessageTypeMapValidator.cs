using Microsoft.Extensions.Hosting;

namespace Common.Infrastructure.Outbox;

/// <summary>
/// Resolves <see cref="MessageTypeMap"/> once, at startup, so its duplicate-name
/// check runs before the host reports ready.
/// </summary>
/// <remarks>
/// <b>Without this the map's whole "fails the host" property is untrue</b>, and
/// §9.4 and the registration comment both promised it. The map is registered
/// through a factory, and a factory is lazy: <c>ValidateOnBuild</c> checks that
/// the call site *can* be satisfied and never invokes it, and the first real
/// resolve happens inside <c>OutboxDispatcher.ProcessBatchAsync</c> — after a
/// row has been claimed, on a background thread, in a host that has been
/// serving traffic for as long as the outbox stayed empty. Two types sharing a
/// <c>FullName</c> would surface there rather than at boot, which is the exact
/// failure the constructor's throw exists to prevent.
/// <para>
/// A hosted service rather than eager construction in the registration,
/// because the source is deliberately mutable: §12.4's fixture adds the test
/// assembly after <c>AddCatalogInfrastructure</c> has run, and a map built
/// during registration would freeze the production list before that line.
/// <c>StartAsync</c> runs once the container is built and every registration
/// is final, which is the first moment the answer is the real one.
/// </para>
/// <para>
/// Registered <b>before</b> the dispatcher, because hosted services start in
/// registration order: a bad map should stop the host rather than let a poll
/// loop begin against it.
/// </para>
/// </remarks>
public sealed class MessageTypeMapValidator(MessageTypeMap types) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Resolving it is the whole job — the constructor throws on a
        // duplicate, and injecting it here is what forces that constructor.
        // The property read keeps the parameter from being an unused
        // dependency a later reader might delete.
        _ = types.StageableDomainEvents;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
