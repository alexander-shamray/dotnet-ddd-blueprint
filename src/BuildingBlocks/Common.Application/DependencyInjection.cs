using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Scrutor;

namespace Common.Application;

/// <summary>
/// What a service's <c>Add&lt;Service&gt;Application</c> composes (§4.2). The
/// pipeline behaviours are not here: registration order is pipeline order
/// (§6.3), and the order belongs to the service that declares it.
/// </summary>
public static class DependencyInjection
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Registers the dispatcher. Scoped, because handlers are — a singleton
        /// dispatcher would capture the root provider and hand every request the
        /// same handler, and the same unit of work behind it.
        /// </summary>
        /// <remarks>
        /// This exists because <c>Dispatcher</c> is internal to this assembly
        /// (§6.2). A service cannot name the type, so it cannot write the
        /// <c>AddScoped</c> line itself.
        /// </remarks>
        public IServiceCollection AddDispatcher()
        {
            services.AddScoped<IDispatcher, Dispatcher>();
            return services;
        }

        /// <summary>
        /// Registers §7.5's dispatcher and the projection registry behind it.
        /// Both scoped: the registry resolves scoped handlers, and the
        /// dispatcher runs inside the command's transaction.
        /// </summary>
        /// <remarks>
        /// This exists for the same reason <see cref="AddDispatcher"/> does —
        /// both implementations are internal to this assembly, so a service
        /// cannot write the <c>AddScoped</c> lines itself. §7.5 prints them
        /// bare because it is describing the registrations rather than the
        /// accessibility; the method is where they actually live.
        /// </remarks>
        public IServiceCollection AddDomainEventDispatcher()
        {
            // Singleton, and the one lifetime on this line that is not
            // obvious: the memo is keyed to the container, not to the scope
            // that first asked (ProjectionRegistryCache argues why).
            services.AddSingleton<ProjectionRegistryCache>();
            services.AddScoped<IProjectionRegistry, ProjectionRegistry>();
            services.AddScoped<IDomainEventDispatcher, DomainEventDispatcher>();
            return services;
        }

        /// <summary>
        /// Registers every implementation of a <see cref="PluggableInterfaces"/>
        /// contract found in <paramref name="assembly"/>.
        /// </summary>
        /// <remarks>
        /// <b>Each layer scans itself.</b> Handlers do not all live in
        /// Application — projections write SQL, cache invalidators sit in
        /// Infrastructure, and command mappers convert wire contracts. Scanning one
        /// assembly registers some handlers and silently skips the rest, so both
        /// registration methods call this (§6.2).
        /// </remarks>
        public IServiceCollection AddPluggableFrom(Assembly assembly) =>
            services.Scan(scan =>
            {
                IImplementationTypeSelector from = scan.FromAssemblies(assembly);

                foreach (Type contract in PluggableInterfaces.All)
                    from
                        .AddClasses(c => c.AssignableTo(contract))
                        .AsImplementedInterfaces()
                        .WithScopedLifetime();
            });
    }
}
