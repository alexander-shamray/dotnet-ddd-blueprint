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
