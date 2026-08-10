using Common.Application;
using Common.Domain;

namespace Catalog.TestSupport.Outbox;

/// <summary>
/// The <c>Local</c> lane's three cases, as real domain events in an assembly
/// the fixture adds to <c>MessageTypeSource</c> (§9.4). Catalog registers no
/// projection handler of its own — §8.4's cache invalidator needs a cached
/// query to invalidate, and there is not one yet — so the lane's behaviour is
/// proven here rather than by inventing a read model for it.
/// </summary>
/// <remarks>
/// In <c>Catalog.TestSupport</c> rather than either test project, on §4.1's
/// terms: the two suites cannot reference each other, and the fixture that
/// registers them is here.
/// </remarks>
public sealed record AlwaysThrows : IDomainEvent
{
    public DateTimeOffset OccurredAt { get; init; }
}

public sealed record NoOpEvent : IDomainEvent
{
    public DateTimeOffset OccurredAt { get; init; }

    /// <summary>
    /// Somewhere to put a payload longer than §7.2's 400-character string
    /// convention, which the outbox's <c>Payload</c> column has to outgrow.
    /// </summary>
    public string Note { get; init; } = "";
}

/// <summary>
/// Has no handler at all, which is the state §9.4 throws on: a <c>Local</c>
/// row was staged, so a handler <em>was</em> found earlier, so finding none
/// now means one was implemented and never registered.
/// </summary>
public sealed record UnhandledEvent : IDomainEvent
{
    public DateTimeOffset OccurredAt { get; init; }
}

/// <summary>Fails every delivery, so a row backs off and accumulates attempts.</summary>
public sealed class AlwaysThrowsProjection : IProjectionHandler<AlwaysThrows>
{
    public Task HandleAsync(AlwaysThrows domainEvent, CancellationToken ct) =>
        throw new InvalidOperationException("this projection always throws");
}

/// <summary>Succeeds, so a row beside a failing one still completes.</summary>
public sealed class NoOpProjection : IProjectionHandler<NoOpEvent>
{
    public Task HandleAsync(NoOpEvent domainEvent, CancellationToken ct) => Task.CompletedTask;
}
