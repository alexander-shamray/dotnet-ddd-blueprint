using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Common.Contracts;
using Common.Contracts.Ordering.V1;
using Common.Contracts.Payments.V1;
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
        // the shape of the contract does not settle it. A positional record
        // takes its values in a primary constructor — and can still declare an
        // extra init property beside them, which a caller may omit:
        //
        //     record C(Guid Id) { public string? Note { get; init; } }
        //
        // So the question is asked of every writable property rather than of
        // the type: it must be `required`, or supplied by every public
        // constructor. Judging by constructor shape skipped that case
        // entirely — the same fail-open the discovery predicate above had
        // twice, a check that judges less than it claims to.
        foreach (Type type in Contracts)
        {
            string[] optional =
            [
                .. type
                    .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(p => p.SetMethod is not null && !IsAlwaysSupplied(p, type))
                    .Select(p => $"{type.Name}.{p.Name}")
            ];

            optional.ShouldBeEmpty(
                $"{type.FullName} can be constructed without these, so a producer can omit them " +
                "and every consumer reads a default (§12.6)");
        }
    }

    /// <summary>
    /// Whether a property cannot be left unset: either it is <c>required</c>,
    /// or every public constructor takes it.
    /// </summary>
    /// <remarks>
    /// <b>Every</b> constructor, not any. One overload that omits the parameter
    /// is one way to build the contract without the value, which is the whole
    /// of what this rule forbids.
    /// </remarks>
    private static bool IsAlwaysSupplied(PropertyInfo property, Type type)
    {
        if (property.IsDefined(typeof(RequiredMemberAttribute), inherit: false))
            return true;

        ConstructorInfo[] constructors = type.GetConstructors();

        return constructors.Length > 0 &&
            constructors.All(c => c.GetParameters().Any(p =>
                string.Equals(p.Name, property.Name, StringComparison.OrdinalIgnoreCase) &&
                p.ParameterType == property.PropertyType));
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

    /// <summary>
    /// The spellings a subject identifier has reached this repository under.
    /// Matched as a substring of the property name, so <c>CustomerId</c>,
    /// <c>BuyerId</c> and a bare <c>Customer</c> all land.
    /// </summary>
    /// <remarks>
    /// <b>A list, and therefore incomplete by construction</b> — the reason
    /// <see cref="A_subject_is_detectable_on_a_contract_that_carries_one"/>
    /// sits beside this one. A reviewer adding a subject under a spelling
    /// nobody predicted gets past this gate, and the positive control is what
    /// keeps the gate from being uninformative rather than what closes that.
    /// </remarks>
    private static readonly string[] SubjectSpellings =
    [
        "Customer",
        "Buyer",
        "Payer",
        "Subject",
        "User",
        "Principal"
    ];

    /// <summary>
    /// Every contract that does not implement <see cref="IIntegrationEvent"/>.
    /// </summary>
    /// <remarks>
    /// <b>That is a superset of the commands, and the name is the nearest
    /// short word rather than an exact one.</b> §9.1 states the one-way
    /// implication — <c>CancelOrder</c>'s remarks say commands "deliberately do
    /// not implement <see cref="IIntegrationEvent"/>" — and the converse does
    /// not follow: this also selects the payload records events carry
    /// (<c>PlacedLine</c>, <c>ConfirmedLine</c>, <c>StockLine</c>,
    /// <c>ShippingAddressV1</c>) and this suite's own discovery probes.
    /// <para>
    /// The widening is in the safe direction and is kept deliberately. ADR-028
    /// is about a subject reaching a decision the receiver cannot check, and a
    /// payload record nested in a command would carry one just as effectively
    /// as a top-level member — so judging more than the commands refuses more,
    /// never less. What it must not do is make the rule *read* as narrower than
    /// it is, which is why this says so rather than calling the set "commands"
    /// and leaving a reader to assume §9.1 licensed it.
    /// </para>
    /// </remarks>
    private static readonly Type[] NonEvents =
    [
        .. Contracts.Where(t => !typeof(IIntegrationEvent).IsAssignableFrom(t))
    ];

    private static PropertyInfo[] SubjectMembers(Type type) =>
    [
        .. type
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => SubjectSpellings.Any(s =>
                p.Name.Contains(s, StringComparison.OrdinalIgnoreCase)))
    ];

    [Fact]
    public void No_command_contract_carries_a_subject()
    {
        // §11.4's subject rule, applied to the path that rule excluded until
        // ADR-028 settled it (#63). A command crossing the broker arrives with
        // no principal, so a subject identifier on one is a decision about
        // whose money moves taken from a field the receiver cannot check. The
        // owning service re-derives the subject from its own record instead.
        //
        // Events are exempt and must be: OrderPlaced carries the CustomerId
        // that IS the record Payments builds from, and it is bound from the
        // principal at Ordering's endpoint before it is ever published.
        (string Command, string Member)[] offenders =
        [
            .. NonEvents
                .SelectMany(t => SubjectMembers(t).Select(p => (t.FullName!, p.Name)))
        ];

        offenders.ShouldBeEmpty(
            "a command carries no principal, so a subject on one is unverifiable " +
            "at the receiver (ADR-028): " +
            string.Join(", ", offenders.Select(o => $"{o.Command}.{o.Member}")));
    }

    [Fact]
    public void A_subject_is_detectable_on_a_contract_that_carries_one()
    {
        // The positive control, and it is not decoration. The test above
        // passes if SubjectMembers matches nothing at all — a misspelt
        // spelling list, a BindingFlags mistake, a Contracts array that went
        // empty — and an empty offender set reads identically either way.
        // This repository's most-repeated failure is a gate that quietly stops
        // covering its surface, so the detector is pointed at a type known to
        // carry a subject and required to find it.
        //
        // OrderPlaced is that type BY DESIGN rather than by accident: it is
        // the event Payments builds its record of the payer from (§3.2), so it
        // is the one contract whose CustomerId ADR-028 requires to stay.
        SubjectMembers(typeof(OrderPlaced))
            .Select(p => p.Name)
            .ShouldContain(nameof(OrderPlaced.CustomerId));
    }

    [Fact]
    public void The_set_the_subject_gate_reads_holds_the_real_commands()
    {
        // The other half of the same argument, one level up: the control above
        // proves the detector works and says nothing about what it is pointed
        // at. NonEvents is a filter over Contracts, and a filter that selects
        // nothing makes No_command_contract_carries_a_subject vacuous while
        // leaving it green.
        //
        // **Not merely non-empty, and the difference is this suite's own
        // probes.** NonEvents legitimately holds payload records and the two
        // discovery probes UnversionedProbe and NestingProbe.NestedProbe, so a
        // ShouldNotBeEmpty here would still pass with every real command
        // filtered out — a control satisfied by the fixtures it was meant to
        // see past. Name the commands the rule exists for instead.
        NonEvents.ShouldContain(typeof(AuthorisePayment));
        NonEvents.ShouldContain(typeof(CancelOrder));
        NonEvents.ShouldContain(typeof(ConfirmOrder));

        // And the filter must exclude something, or it is not a filter and the
        // gate is being applied to events it must not be applied to.
        NonEvents.ShouldNotContain(
            typeof(OrderPlaced),
            "events are exempt from the subject rule, and OrderPlaced is the one " +
            "ADR-028 requires to keep its CustomerId");
    }

    private static string Names(IEnumerable<Type> types) =>
        string.Join(", ", types.Select(t => t.FullName));
}
