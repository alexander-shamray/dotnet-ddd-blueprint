using Common.Application;

namespace Catalog.Infrastructure;

/// <summary>
/// The truthful <see cref="IDomainEventDispatcher"/> while Catalog can raise
/// no domain event: the domain has no aggregate until PR-10, and there is no
/// outbox to stage into until PR-14. PR-14's real dispatcher replaces this
/// class — from PR-10 until then, any event an aggregate raises is dropped
/// here, which that PR's slice must weigh.
/// </summary>
internal sealed class NullDomainEventDispatcher : IDomainEventDispatcher
{
    public Task DispatchAsync(CancellationToken ct) => Task.CompletedTask;
}
