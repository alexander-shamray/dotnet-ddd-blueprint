using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Common.Contracts;
using Common.Contracts.Inventory.V1;
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
/// <c>required</c> members, and — since ADR-028 — that a command carries no
/// subject. Each is mechanical, so each is a test rather than a review note.
/// </summary>
/// <remarks>
/// <b>The fourth is the one whose gate needs a gate.</b> The first three fail
/// against a type that is <em>present</em> — a domain type named, a namespace
/// misspelt, a member not <c>required</c>. The subject rule asserts an
/// <em>absence</em>, so an empty result is both what success looks like and
/// what a broken detector looks like, which is why it ships with two controls
/// rather than alone.
/// </remarks>
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
    /// Every contract type an integration event carries, transitively — the
    /// line types, and anything those carry in turn.
    /// </summary>
    /// <remarks>
    /// Computed rather than listed, because a list here is a second inventory
    /// to reconcile and this one changes whenever a contract gains a member.
    /// </remarks>
    private static readonly HashSet<Type> CarriedByAnEvent = BuildEventPayloadClosure();

    /// <summary>
    /// The command roots and the payload types only a command carries.
    /// </summary>
    /// <remarks>
    /// <b>"Not an event" is not the same as "a command", and the difference is
    /// not merely pedantic — it decides whether this gate can refuse a legal
    /// contract.</b> §9.1 states the one-way implication only: commands
    /// "deliberately do not implement <see cref="IIntegrationEvent"/>". The
    /// converse would sweep in the payload records events carry —
    /// <c>PlacedLine</c>, <c>ConfirmedLine</c>, <c>ShippingAddressV1</c> — and
    /// an event is **allowed** a subject. <c>OrderPlaced</c> carries the
    /// <c>CustomerId</c> ADR-028 requires it to keep; an event that factored
    /// the same field into its line type would be doing something the rule
    /// permits, and a gate over every non-event would fail the build for it.
    /// <para>
    /// So the subtraction is the definition. Everything reachable from an
    /// event's property graph is exempt, exactly as the event itself is, and
    /// what remains is the commands plus the payloads only they carry.
    /// <c>StockLine</c> stays judged — <c>ReserveStock</c> is a command — which
    /// is the half that must not be lost: a subject nested one level down in a
    /// command reaches the same decision as a top-level one.
    /// </para>
    /// <para>
    /// An earlier revision judged every non-event and called the widening safe
    /// on the grounds that it "refuses more, never less". True of coverage and
    /// false of correctness: refusing more includes refusing shapes the rule
    /// allows.
    /// </para>
    /// </remarks>
    private static readonly Type[] Commands =
    [
        .. Contracts
            .Where(t => !typeof(IIntegrationEvent).IsAssignableFrom(t))
            .Where(t => !CarriedByAnEvent.Contains(t))
    ];

    private static HashSet<Type> BuildEventPayloadClosure()
    {
        HashSet<Type> carried = [];
        Queue<Type> pending = new(
            Contracts.Where(t => typeof(IIntegrationEvent).IsAssignableFrom(t)));

        while (pending.Count > 0)
        {
            foreach (Type next in CarriedContractTypes(pending.Dequeue()))
            {
                if (carried.Add(next))
                    pending.Enqueue(next);
            }
        }

        return carried;
    }

    private static IEnumerable<Type> CarriedContractTypes(Type type) =>
        type
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => ElementType(p.PropertyType))
            .Where(Contracts.Contains);

    /// <summary>
    /// The type a member contributes to the payload graph: an element type for
    /// a collection, the type itself otherwise.
    /// </summary>
    private static Type ElementType(Type type)
    {
        if (type == typeof(string))
            return type;

        if (type.IsArray)
            return type.GetElementType()!;

        if (type.IsGenericType && type.GetGenericArguments() is [Type single])
            return single;

        return type;
    }

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
            .. Commands
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
        // **Not merely non-empty, and the difference is StockLine.** Commands
        // legitimately holds a payload record only a command carries, so a
        // ShouldNotBeEmpty here would still pass with every command root
        // filtered out. Name the roots the rule exists for instead.
        //
        // **All seven of them, which is §3.2's Accepts columns read across.**
        // An earlier revision named three and would have stayed green while
        // discovery silently dropped the other four — the coverage failure
        // this repository keeps rediscovering, reproduced inside the control
        // written to prevent it.
        Type[] commandRoots =
        [
            typeof(AuthorisePayment),
            typeof(CancelOrder),
            typeof(ConfirmOrder),
            typeof(MarkOrderShipped),
            typeof(FlagOrderForReview),
            typeof(ReserveStock),
            typeof(ReleaseStock)
        ];

        foreach (Type root in commandRoots)
            Commands.ShouldContain(root);

        // A payload only a command carries stays judged: a subject one level
        // down reaches the same decision as a top-level one.
        Commands.ShouldContain(typeof(StockLine));

        // And the exemptions must actually be excluded, or the gate is being
        // applied to events — which ADR-028 permits a subject.
        Commands.ShouldNotContain(
            typeof(OrderPlaced),
            "events are exempt, and OrderPlaced is the one ADR-028 requires to keep its CustomerId");

        Commands.ShouldNotContain(
            typeof(PlacedLine),
            "a line type an event carries is part of that event, so it inherits the exemption");
    }

    private static string Names(IEnumerable<Type> types) =>
        string.Join(", ", types.Select(t => t.FullName));
}
