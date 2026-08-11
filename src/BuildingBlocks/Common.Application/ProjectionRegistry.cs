using System.Collections.Concurrent;
using Common.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Common.Application;

/// <summary>
/// The memo <see cref="ProjectionRegistry"/> reads. A singleton, so its
/// lifetime is the container's and not the process's.
/// </summary>
/// <remarks>
/// <b>Not a <c>static</c> field on the registry, and the difference is a test
/// that lies.</b> §7.5 argues the cache is safe because DI registrations do
/// not change at runtime, which is true of one container and false of a
/// process holding several: two <c>WebApplicationFactory</c> hosts in one test
/// assembly, or a host beside a bare <c>ServiceCollection</c>, would share
/// whichever answer was computed first — so a suite proving that an event with
/// no handler stages no <c>Local</c> row would poison the suite proving that
/// one with a handler does, in whichever order they happened to run. Keyed to
/// the container, the memo still answers a question about registrations rather
/// than about instances, which is the property that made it safe in the first
/// place.
/// </remarks>
internal sealed class ProjectionRegistryCache
{
    public ConcurrentDictionary<Type, bool> HasHandler { get; } = new();
}

/// <summary>
/// Derived from the DI container rather than a hand-maintained list, so it
/// cannot drift from what is actually registered (§6.2).
/// </summary>
/// <remarks>
/// <b>Registered scoped, never singleton.</b> Handlers are scoped (§6.2), and
/// <c>GetServices</c> for a scoped service from the root provider throws
/// <em>"Cannot resolve scoped service from root provider"</em>.
/// </remarks>
internal sealed class ProjectionRegistry(IServiceProvider services, ProjectionRegistryCache cache)
    : IProjectionRegistry
{
    public bool HasHandler(IDomainEvent domainEvent) =>
        cache.HasHandler.GetOrAdd(
            domainEvent.GetType(),
            type => services.GetServices(typeof(IProjectionHandler<>).MakeGenericType(type)).Any());
}
