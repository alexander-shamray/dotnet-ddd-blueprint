using Common.Application;

namespace Catalog.Application;

/// <summary>
/// Drops what it is handed, and since PR-10 it is handed something real:
/// every <c>Product.Publish</c> raises a <c>ProductPublishedDomainEvent</c>
/// that ends here, because there is no outbox to stage into until PR-14 —
/// whose real dispatcher replaces this class. The drop is stated in
/// CLAUDE.md's phase note rather than hidden; the aggregate raises anyway,
/// so PR-14 picks the events up without touching the domain (§5.5). In
/// Application because §4.2's registration sample puts the real one there,
/// beside the dispatcher it serves.
/// </summary>
internal sealed class NullDomainEventDispatcher : IDomainEventDispatcher
{
    public Task DispatchAsync(CancellationToken ct) => Task.CompletedTask;
}
