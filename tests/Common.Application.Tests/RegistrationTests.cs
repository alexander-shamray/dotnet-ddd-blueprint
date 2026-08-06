using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Common.Application.Tests;

/// <summary>
/// The §6.2 trap: nothing in C# requires an implemented interface to be
/// resolvable, and <c>GetServices&lt;T&gt;()</c> returning empty is
/// indistinguishable from "there is no handler for this". The container starts,
/// the monitoring stays green, and the work is silently not done.
/// </summary>
public class RegistrationTests
{
    [Fact]
    public void Every_handler_implementation_is_registered()
    {
        // Handlers are scoped; resolving them from the root provider throws.
        using ServiceProvider provider = TestContainer.Build();
        using IServiceScope scope = provider.CreateScope();

        // The same list the scan reads — a new interface is covered the moment
        // it is added to PluggableInterfaces, with no second place to remember.
        // The previous version of §6.2 kept two copies of this list, and both
        // drifted together or not at all.
        IEnumerable<(Type Implementation, Type Service)> implementations =
            typeof(Ping).Assembly
                .GetTypes()
                .Where(t => t is { IsAbstract: false, IsInterface: false })
                .SelectMany(t => t
                    .GetInterfaces()
                    .Where(i => i.IsGenericType &&
                        PluggableInterfaces.All.Contains(i.GetGenericTypeDefinition()))
                    .Select(i => (Implementation: t, Service: i)));

        foreach (var (implementation, service) in implementations)
            scope.ServiceProvider.GetServices(service).ShouldContain(
                s => s!.GetType() == implementation,
                $"{implementation.Name} implements {service.Name} but is not registered.");
    }

    [Fact]
    public void The_scan_finds_something_to_register()
    {
        // Without this the test above is satisfied by a scan that found no
        // implementations at all — an empty sequence passes every assertion
        // made about its members.
        using ServiceProvider provider = TestContainer.Build();
        using IServiceScope scope = provider.CreateScope();

        scope.ServiceProvider.GetServices<ICommandHandler<Ping, string>>().ShouldNotBeEmpty();
        scope.ServiceProvider.GetServices<IQueryHandler<Ask, string>>().ShouldNotBeEmpty();
    }

    [Fact]
    public void The_pipeline_behaviour_interface_is_not_one_of_the_scanned_ones()
    {
        // Registration order is pipeline order (§6.3) and a scan gives no
        // ordering guarantee, so this one interface is excluded on purpose.
        // Adding it here would compile, pass every other test, and reorder the
        // pipeline into whatever order reflection returns types.
        PluggableInterfaces.All.ShouldNotContain(typeof(IPipelineBehavior<,>));
    }

    [Fact]
    public void Handlers_are_registered_with_a_scoped_lifetime()
    {
        // Scoped, not singleton: a handler holds the unit of work and the
        // repositories of one request (§4.2). A singleton handler would share
        // one DbContext across every request the moment PR-08 puts one behind
        // it, and nothing about the registration would look wrong.
        using ServiceProvider provider = TestContainer.Build();
        using IServiceScope first = provider.CreateScope();
        using IServiceScope second = provider.CreateScope();

        ICommandHandler<Ping, string> a = first.ServiceProvider.GetRequiredService<ICommandHandler<Ping, string>>();
        ICommandHandler<Ping, string> b = second.ServiceProvider.GetRequiredService<ICommandHandler<Ping, string>>();

        a.ShouldNotBeSameAs(b);
    }
}
