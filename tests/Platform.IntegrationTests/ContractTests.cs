using System.Reflection;
using System.Runtime.CompilerServices;
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
    /// <para>
    /// <b>The root namespace is included, and a trailing dot is what excluded
    /// it.</b> <c>StartsWith("Common.Contracts.")</c> reads as "everything in
    /// the assembly" and is not: a concrete type declared directly in
    /// <c>Common.Contracts</c>, with no version namespace at all, fell outside
    /// discovery entirely — so it bypassed the versioned-namespace check, the
    /// sample check, the wire-member check and the round-trip, and left every
    /// test green. That unversioned contract is the exact mistake §9.2 exists
    /// to reject, and it was the one shape this suite could not see.
    /// </para>
    /// </remarks>
    private static readonly Type[] Contracts =
    [
        .. typeof(OrderPlaced).Assembly.GetTypes().Where(IsContract)
    ];

    /// <summary>§9.2's shape: <c>Common.Contracts.&lt;Service&gt;.V&lt;n&gt;</c>.</summary>
    private const string VersionedNamespace = @"^Common\.Contracts\.[A-Za-z]+\.V\d+$";

    /// <summary>
    /// A concrete type visible outside the assembly, anywhere under
    /// <c>Common.Contracts</c> — the root included, which is the half a
    /// trailing dot silently dropped.
    /// </summary>
    /// <remarks>
    /// <b><c>IsVisible</c>, not <c>IsPublic</c>, and the difference is a second
    /// hole of the same kind.</b> <c>IsPublic</c> is false for every nested
    /// type, <em>including</em> one declared <c>public</c> inside a public
    /// class — those report <c>IsNestedPublic</c> instead. A contract nested in
    /// a public type is as reachable by a consumer as any other and would have
    /// fallen out of discovery entirely, bypassing the namespace, sample,
    /// required-member and round-trip checks alike. <c>IsVisible</c> is the
    /// question actually being asked: can something outside this assembly name
    /// it.
    /// </remarks>
    internal static bool IsContract(Type type) =>
        type.IsVisible &&
        type is { IsInterface: false, IsAbstract: false } &&
        type.Namespace is string ns &&
        (ns == "Common.Contracts" || ns.StartsWith("Common.Contracts.", StringComparison.Ordinal));

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
    public void Discovery_sees_a_contract_that_forgot_its_version_namespace()
    {
        // The positive control for the filter above, and it exists because that
        // filter was written with a trailing dot and so could not see the one
        // shape it most needed to: a concrete type declared straight into
        // `Common.Contracts`, with no `V1` at all. Such a type fell out of
        // discovery entirely, which meant the versioned-namespace test never
        // judged it, no sample was demanded for it, and every test stayed green
        // over exactly the mistake §9.2 forbids.
        //
        // Asserted against the predicate rather than the assembly, because the
        // only way to have such a type is to declare one — and declaring it in
        // Common.Contracts would be committing the defect to prove it can be
        // caught. The type below lives in this test assembly, in that namespace.
        IsContract(typeof(Common.Contracts.UnversionedProbe)).ShouldBeTrue(
            "a contract with no version namespace must reach the checks, not slip past them");

        Regex.IsMatch(typeof(Common.Contracts.UnversionedProbe).Namespace!, VersionedNamespace)
            .ShouldBeFalse("and it must then fail the rule it breaks");
    }

    [Fact]
    public void Discovery_sees_a_contract_nested_inside_a_public_type()
    {
        // The second positive control, for the second hole of the same kind.
        // `Type.IsPublic` is false for every nested type — including one
        // declared `public` inside a public class, which reports
        // `IsNestedPublic` instead — so a contract in that position bypassed
        // the namespace, sample, required-member and round-trip checks alike
        // while being as reachable by a consumer as any other.
        //
        // `IsVisible` asks the question that was meant: can something outside
        // this assembly name it.
        IsContract(typeof(Common.Contracts.NestingProbe.NestedProbe)).ShouldBeTrue(
            "a nested public contract is visible to every consumer, so discovery must see it too");

        typeof(Common.Contracts.NestingProbe.NestedProbe).IsPublic.ShouldBeFalse(
            "and IsPublic is the property that says otherwise — which is why it was the wrong one");
    }

    [Fact]
    public void Every_contract_lives_in_a_versioned_namespace()
    {
        // Common.Contracts.<Service>.V<n> — §9.2. A contract that lands one
        // namespace short is a v1 that can never be superseded.
        Contracts.ShouldAllBe(t =>
            Regex.IsMatch(t.Namespace!, VersionedNamespace));
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
    public void No_contract_can_be_constructed_half_filled()
    {
        // The third rule this suite's summary claims and did not enforce.
        // §12.6 calls `required` members mechanical, so this is a test rather
        // than a review note — and removing `required` from a contract property
        // leaves every other assertion here green, because the JSON is
        // unchanged either way. What breaks is a producer's ability to omit the
        // member, which no serialisation test can see.
        //
        // The rule is really "there is no way to build one incompletely", and
        // there are two shapes that satisfy it. A positional record takes its
        // values in a primary constructor and needs no `required` at all —
        // `PlacedLine`, `StockLine`, `ShippingAddressV1`. A property-based
        // record can be built by `new()` and needs every property marked. So
        // the assertion is on the shape that actually has the hole: a contract
        // with a public parameterless constructor must mark every settable
        // property `required`.
        foreach (Type type in Contracts.Where(t => t.GetConstructor(Type.EmptyTypes) is not null))
        {
            string[] optional =
            [
                .. type
                    .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(p => p.SetMethod is not null &&
                        !p.IsDefined(typeof(RequiredMemberAttribute), inherit: false))
                    .Select(p => $"{type.Name}.{p.Name}")
            ];

            optional.ShouldBeEmpty(
                $"{type.FullName} can be constructed without these, so a producer can omit them " +
                "and every consumer reads a default (§12.6)");
        }
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
