using System.Reflection;
using Catalog.Domain.Products;
using NetArchTest.Rules;
using Shouldly;
using Xunit;
using TestResult = NetArchTest.Rules.TestResult;

namespace Catalog.Application.Tests;

/// <summary>
/// The §4.2 gates that reason over type dependencies. Green on an empty
/// skeleton by design — "an architecture rule introduced before the
/// violations exist is a constraint", and these have been observed failing
/// against a deliberately added forbidden reference.
/// </summary>
public class ArchitectureTests
{
    [Fact]
    public void Application_does_not_depend_on_ef_core()
    {
        TestResult result = Types
            .InAssembly(typeof(DependencyInjection).Assembly)
            .ShouldNot().HaveDependencyOn("Microsoft.EntityFrameworkCore")
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(
            $"leaked: {string.Join(", ", result.FailingTypeNames ?? [])}");
    }

    [Fact]
    public void Application_and_domain_do_not_reference_masstransit()
    {
        // §9.3's must-not list. The saga may Send and Publish because MassTransit's
        // in-memory outbox holds those until the consume transaction commits — a
        // guarantee that exists on the consume pipeline and nowhere else. A handler
        // that copies the saga's style gets a dual write with no outbox behind it,
        // and it works in every test where the broker is up.
        Assembly[] assemblies = [typeof(DependencyInjection).Assembly, typeof(Product).Assembly];
        foreach (Assembly assembly in assemblies)
        {
            Types
                .InAssembly(assembly)
                .ShouldNot().HaveDependencyOn("MassTransit")
                .GetResult().IsSuccessful.ShouldBeTrue(assembly.GetName().Name);
        }
    }
}
