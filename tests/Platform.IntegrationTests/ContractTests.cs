using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Common.Contracts;
using Common.Contracts.Ordering.V1;
using Shouldly;
using Xunit;

namespace Platform.IntegrationTests;

/// <summary>
/// §12.6. The saga suites prove one service's coordination; the only thing
/// genuinely <em>between</em> services is the contract assembly, and its rules
/// are all stated elsewhere as things a reviewer should notice — §9.1's "a
/// contract may not name a domain type", §9.2's versioned namespace,
/// <c>required</c> members. Each is mechanical, so each is a test rather than a
/// review note.
/// </summary>
public class ContractTests
{
    /// <summary>
    /// Concrete types only. The assembly also holds <see cref="IIntegrationEvent"/>
    /// (§9.1) and the static code vocabularies (<c>CancelReasons</c>,
    /// <c>ReviewReasons</c>), and a filter of "everything public under
    /// <c>Common.Contracts</c>" would demand a versioned namespace of an
    /// interface deliberately shared across all of them — and then ask
    /// <see cref="ContractSamples"/> for an instance of it.
    /// </summary>
    /// <remarks>
    /// <c>IsAbstract: false</c> does both jobs, and the second is worth
    /// knowing: a C# <c>static class</c> compiles to <c>abstract sealed</c>, so
    /// the vocabularies are excluded by the same clause that excludes a genuine
    /// abstract base, with no name-based special case to keep up to date.
    /// </remarks>
    private static readonly Type[] Contracts =
    [
        .. typeof(OrderPlaced).Assembly
            .GetTypes()
            .Where(t => t.IsPublic &&
                t is { IsInterface: false, IsAbstract: false } &&
                t.Namespace?.StartsWith("Common.Contracts.", StringComparison.Ordinal) == true)
    ];

    [Fact]
    public void No_contract_names_a_domain_type()
    {
        // §9.1's rule, and the one that silently drags a service's Domain into
        // every consumer. Checked at the assembly level because a contract
        // cannot name a domain type without the project reference — which is
        // also why this assertion survives a contract that has not been written
        // yet, where a member-by-member check would not.
        typeof(OrderPlaced).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name!)
            .ShouldNotContain(name => name.EndsWith(".Domain", StringComparison.Ordinal));
    }

    [Fact]
    public void Every_contract_lives_in_a_versioned_namespace()
    {
        // Common.Contracts.<Service>.V<n> — §9.2. A contract that lands one
        // namespace short is a v1 that can never be superseded.
        Contracts.ShouldAllBe(t =>
            Regex.IsMatch(t.Namespace!, @"^Common\.Contracts\.[A-Za-z]+\.V\d+$"));
    }

    [Fact]
    public void Every_contract_has_a_sample()
    {
        // The precondition for the round-trip below, asserted separately so its
        // failure names the missing sample rather than arriving as one message
        // in the middle of a loop. Without it the suite reads as covering
        // everything and covers whatever somebody remembered.
        Type[] unsampled = [.. Contracts.Except(ContractSamples.Sampled)];

        unsampled.ShouldBeEmpty(
            $"every contract needs a ContractSamples entry (§12.6): {Names(unsampled)}");
    }

    [Fact]
    public void No_sample_survives_the_contract_it_was_written_for()
    {
        // The other direction, and the one throwing cannot catch: a sample for
        // a deleted or renamed contract compiles until the type is gone and is
        // dead weight the moment it is. Cheap here, invisible otherwise.
        Type[] orphaned = [.. ContractSamples.Sampled.Except(Contracts)];

        orphaned.ShouldBeEmpty($"these samples name types no longer public contracts: {Names(orphaned)}");
    }

    [Fact]
    public void Every_contract_round_trips_through_the_bus_serialiser()
    {
        // Catches the member type System.Text.Json cannot handle — the failure
        // that otherwise appears as a message in the error queue, in staging,
        // with a deserialisation stack trace and no obvious owner.
        //
        // Default options on purpose, unlike §9.4's outbox round-trip: the
        // outbox is this service's own format and takes the registered
        // OutboxJson, converters included, while a contract crosses to a
        // consumer that configures its own serialiser. A contract that needs a
        // converter to survive is a contract that has stopped being primitives.
        foreach (Type type in Contracts)
        {
            object instance = ContractSamples.Create(type);
            string json = JsonSerializer.Serialize(instance, type);
            object? returned = JsonSerializer.Deserialize(json, type);

            JsonSerializer.Serialize(returned, type).ShouldBe(json, type.FullName);
        }
    }

    [Fact]
    public void Every_contract_member_reaches_the_wire()
    {
        // The half the round-trip above cannot see. It compares one serialised
        // form against another, so a member that fails to serialise at all is
        // absent from both and the comparison passes — the contract loses a
        // field and the suite says nothing. Asking for the declared names is
        // what closes that, and it is also the assertion that fails when a
        // member is added to a record and not to its sample.
        foreach (Type type in Contracts)
        {
            object instance = ContractSamples.Create(type);
            using JsonDocument document =
                JsonDocument.Parse(JsonSerializer.Serialize(instance, type));

            string[] onTheWire = [.. document.RootElement.EnumerateObject().Select(p => p.Name)];

            string[] declared =
            [
                .. type
                    .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(p => p.GetIndexParameters().Length == 0)
                    .Select(p => p.Name)
            ];

            onTheWire.ShouldBe(declared, ignoreOrder: true, type.FullName);
        }
    }

    private static string Names(IEnumerable<Type> types) =>
        string.Join(", ", types.Select(t => t.FullName));
}
