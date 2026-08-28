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
/// what a broken detector looks like. It therefore ships with controls rather
/// than alone — no count here, because that number has already been wrong
/// twice as review added to it.
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
                    // Fully qualified, because the exemption list is — §9.2 has
                    // two versions of a contract live at once during a
                    // deprecation, so a simple name cannot say which one an
                    // entry is about. Both sides have to agree or the Except
                    // below silently stops matching and un-exempts the member.
                    .Select(p => $"{type.FullName}.{p.Name}")
            ];

            // Subtracted from the FAILURES rather than from the candidates, so
            // there is no narrowed selection to pass vacuously — the same shape
            // the composition-root gate ended up in after three attempts at
            // filtering what it looked at.
            string[] unexplained = [.. optional.Except(AdditiveMembers)];

            unexplained.ShouldBeEmpty(
                $"{type.FullName} can be constructed without these, so a producer can omit them " +
                "and every consumer reads a default (§12.6)");
        }
    }

    /// <summary>
    /// Members added to a contract that was already live. They are optional
    /// for the life of that contract version, and the entry clears when the
    /// version does (§9.2).
    /// </summary>
    /// <remarks>
    /// <b>This list exists because the rule above and §9.2 could not both be
    /// obeyed, and the first additive member found it.</b> §9.2 says a new
    /// optional field is additive and needs no version bump; the rule above
    /// says no contract may be constructible half-filled. Measured rather than
    /// argued: <c>System.Text.Json</c> throws
    /// <c>JsonException: … was missing required properties</c>, so a member
    /// shipped as <c>required</c> faults any payload that predates it.
    /// <b>The safe shape is the one the rule forbade</b>, so the rule admits
    /// it by name instead of everywhere.
    /// <para>
    /// <b>It is optional for the LIFE of the contract, not for the length of a
    /// deploy — and an earlier revision of this comment had that wrong.</b> It
    /// called the exemption §15.5's expand phase and said a contract phase was
    /// owed that would make the member <c>required</c>. That later tightening
    /// is a <b>breaking change inside V1</b>: a payload predating the field has
    /// no bound on how long it can survive — <c>docs/runbooks/error-queue.md</c>
    /// says a message waits there until somebody handles it, outliving even
    /// its outbox row's purge, and a replay can reintroduce it at any time —
    /// so making the member <c>required</c> would fail deserialisation before
    /// any consumer branch could apply the absent-value reading. §9.2 sends a
    /// breaking change to a new version, so the tightening, if it is ever
    /// wanted, is a V2 rather than an edit to this one.
    /// </para>
    /// <para>
    /// <b>It still clears itself; the trigger is the contract's retirement
    /// rather than the member's tightening.</b> The companion test below fails
    /// when an entry names no public contract, so a V2 replacing V1 forces the
    /// entry out — and it fails the other way too, if a member somehow becomes
    /// always-supplied. A list of deliberate gaps is only honest while
    /// something re-checks that they are still gaps, which is the shape
    /// <c>awaiting-signal.yaml</c>'s unloaded alerts are in.
    /// </para>
    /// </remarks>
    private static readonly string[] AdditiveMembers =
    [
        // #123. Absent means "published before this field existed", and §9.6's
        // saga discards on it — permanently, because a payload that old can
        // still arrive from the error queue or a replay. It leaves this list
        // when V1 is retired, which is what the fully qualified key makes
        // checkable.
        "Common.Contracts.Ordering.V1.OrderCancelled.Origin"
    ];

    [Fact]
    public void A_payload_predating_an_additive_member_still_deserialises()
    {
        // **The property the whole exemption rests on, measured rather than
        // assumed.** §9.6 discards an OrderCancelled whose Origin is absent, on
        // the reading that absent means "published before the field existed" —
        // and that branch is only reachable if the payload deserialises at all.
        // System.Text.Json refuses a missing `required` member outright, so had
        // this member shipped required the message would fault before any saga
        // branch saw it, and the discard would be unreachable code beside a
        // chapter describing it.
        //
        // A hand-written payload rather than a serialised sample with the field
        // removed: what is being modelled is a producer that never knew the
        // member, and a round-trip through today's contract cannot produce one.
        string beforeTheField = """
            {"MessageId":"0199a1e0-0000-7000-8000-000000000001",
             "CorrelationId":"0199a1e0-0000-7000-8000-000000000002",
             "OccurredAt":"2026-08-25T12:00:00+00:00",
             "OrderId":"0199a1e0-0000-7000-8000-000000000003",
             "CustomerId":"0199a1e0-0000-7000-8000-000000000004",
             "Reason":"customer_request"}
            """;

        OrderCancelled? deserialised = JsonSerializer.Deserialize<OrderCancelled>(beforeTheField);

        deserialised.ShouldNotBeNull();
        deserialised.Origin.ShouldBeNull("absent is what §9.6's discard branch reads");
        deserialised.Reason.ShouldBe(CancelReasons.CustomerRequest);
    }

    [Fact]
    public void Every_additive_member_is_still_additive()
    {
        // The gate on the list, without which the list is where the rule above
        // goes to die: an entry outlives the contract it was written for,
        // nothing says so, and a name that resolves to nothing reads exactly
        // like a live exemption. This fails from both directions — a name that
        // no longer resolves, which is what retiring a version produces, and a
        // member that has somehow become always-supplied.
        //
        // **Keyed by the FULLY QUALIFIED name, because §9.2 has two versions
        // live at once during a deprecation.** A simple name resolved with
        // SingleOrDefault does not merely exempt the wrong one — it throws the
        // moment `Ordering.V2.OrderCancelled` exists beside V1's, so this gate
        // would fail for a reason that has nothing to do with what it checks,
        // and the clearing story above ("the entry goes when the version does")
        // could never actually be reached. The version is the whole point of
        // the entry, so it belongs in the key.
        foreach (string entry in AdditiveMembers)
        {
            int split = entry.LastIndexOf('.');
            split.ShouldBeGreaterThan(0, $"{entry} must be spelt Namespace.Type.Member");

            string typeName = entry[..split];
            string memberName = entry[(split + 1)..];

            Type? type = Contracts.SingleOrDefault(t => t.FullName == typeName);
            type.ShouldNotBeNull(
                $"{entry} names no public contract — that version has been retired " +
                "and the entry belongs in the commit that retired it");

            PropertyInfo? property = type.GetProperty(memberName, BindingFlags.Public | BindingFlags.Instance);
            property.ShouldNotBeNull($"{entry} names no member of {type.Name}");

            IsAlwaysSupplied(property, type).ShouldBeFalse(
                $"{entry} is now always supplied, so this entry describes something " +
                "that is no longer true (§12.6)");
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
    /// The command roots of a type universe: a member of it that is not an
    /// event and that nothing else in it carries.
    /// </summary>
    /// <remarks>
    /// Nothing carries <c>ReserveStock</c>, so it is a root; <c>StockLine</c>
    /// is carried by it and is therefore a payload rather than a root. The
    /// seven this resolves to over the real contracts are §3.2's Accepts
    /// columns read across, and
    /// <see cref="The_set_the_subject_gate_reads_holds_the_real_commands"/>
    /// names them so that discovery losing one is a failure rather than a
    /// quietly smaller judged set.
    /// </remarks>
    private static Type[] RootsOf(IReadOnlyCollection<Type> universe)
    {
        HashSet<Type> carried = [.. universe.SelectMany(t => CarriedContractTypes(t, universe))];

        return
        [
            .. universe
                .Where(t => !typeof(IIntegrationEvent).IsAssignableFrom(t))
                .Where(t => !carried.Contains(t))
        ];
    }

    /// <summary>
    /// What the subject rule judges: the command roots, and everything a
    /// command carries transitively.
    /// </summary>
    /// <remarks>
    /// <b>Built <em>up</em> from the commands, not subtracted from the
    /// contracts, and the two are not equivalent.</b> §9.1 states one
    /// implication only — commands "deliberately do not implement
    /// <see cref="IIntegrationEvent"/>" — so neither "every contract" nor
    /// "every non-event" is the judged set:
    /// <list type="bullet">
    /// <item><b>Every non-event refuses shapes the rule allows.</b> It sweeps
    /// in the line types events carry, and an event is *permitted* a subject —
    /// <c>OrderPlaced</c> carries the <c>CustomerId</c> ADR-028 requires it to
    /// keep. An event that factored that field into its line type would fail a
    /// build for doing something legal.</item>
    /// <item><b>Non-events minus the event closure lets one through.</b> That
    /// was the fix for the first problem and it created a worse one: a payload
    /// carried by *both* a command and an event became exempt because an event
    /// reached it — so a subject inside it would travel on the command,
    /// unjudged. A false negative on the exact path this rule exists to close.
    /// </item>
    /// </list>
    /// <para>
    /// Reachability from a command root settles both. A shared payload is
    /// judged, because a command reaches it; a purely-event payload is not,
    /// because no command does. <c>StockLine</c> is judged via
    /// <c>ReserveStock</c> — a subject one level down reaches the same decision
    /// as a top-level one — and <c>PlacedLine</c> is not.
    /// </para>
    /// <para>
    /// The consequence for a shared payload is worth stating rather than
    /// leaving implicit: a type carried by a command and an event alike may not
    /// carry a subject, because the command side forbids what the event side
    /// permits. The stricter rule wins, which is the direction a gate must fail
    /// in.
    /// </para>
    /// </remarks>
    /// <summary>
    /// The command roots the subject rule judges — §3.2's Accepts columns read
    /// across, declared rather than inferred.
    /// </summary>
    /// <remarks>
    /// <b>Inference alone fails open, and the shape is the one this gate has
    /// already been caught by twice.</b> <see cref="RootsOf"/> calls a
    /// non-event a root when nothing else carries it, so an <em>event</em>
    /// declaring a property of a command's type removes that command from the
    /// roots — and nothing then reaches it, because only events do. A subject
    /// on a command dispatched to its own queue would travel unjudged, which is
    /// the shared-payload false negative one level up.
    /// <para>
    /// So the judged set is built from this list, and
    /// <see cref="Inferred_command_roots_and_the_declared_list_agree"/> pins
    /// the list against inference in <b>both</b> directions: a command added to
    /// the contracts and not here fails, and a command that inference loses
    /// fails. A declared list nothing checks is the drift this repository
    /// closes by declaring once and asserting the copies match — the list is
    /// the assertion, and inference is what audits it.
    /// </para>
    /// </remarks>
    private static readonly Type[] DeclaredCommandRoots =
    [
        typeof(AuthorisePayment),
        typeof(CancelOrder),
        typeof(ConfirmOrder),
        typeof(MarkOrderShipped),
        typeof(FlagOrderForReview),
        typeof(ReserveStock),
        typeof(ReleaseStock)
    ];

    /// <summary>
    /// Every non-event contract that is <em>not</em> a command root — the
    /// payload records, each named with what carries it.
    /// </summary>
    /// <remarks>
    /// <b>This list exists to make the classification exhaustive, which is the
    /// only thing that closes the gate's last fail-open.</b> Pairing declared
    /// roots against <see cref="RootsOf"/> catches a command inference loses
    /// and a command nobody declared — but not one that is <em>both</em>: a new
    /// contract carried only by an event and absent from
    /// <see cref="DeclaredCommandRoots"/> drops out of both sides, so the
    /// equality holds and no gate ever inspects it.
    /// <para>
    /// No structural test can settle it, because "is this type dispatched as a
    /// command" is not a fact the type system holds — §9.1 defines a command by
    /// what it does <em>not</em> implement, and a positive marker only moves
    /// the forgetting to the marker. What can be settled is that every contract
    /// has been <b>classified by somebody</b>. A type in neither list fails the
    /// build, which is the scaffold's rule one assembly over: a tool refusing
    /// input it has never been shown beats one that guesses.
    /// </para>
    /// </remarks>
    private static readonly Type[] DeclaredPayloads =
    [
        typeof(StockLine),          // ReserveStock — judged, a command reaches it
        typeof(PlacedLine),         // OrderPlaced — exempt, only an event reaches it
        typeof(ConfirmedLine)       // OrderConfirmed — the same
    ];

    private static readonly Type[] Commands = JudgedTypesOf(Contracts, DeclaredCommandRoots);

    /// <summary>
    /// The judged set of a type universe: its command roots, plus everything
    /// those carry transitively.
    /// </summary>
    /// <remarks>
    /// A function of a universe rather than a fixed field, so that
    /// <see cref="A_payload_shared_by_a_command_and_an_event_stays_judged"/>
    /// can drive the same algorithm with synthetic types. The real contracts
    /// have no shared payload today, so without that the regression this
    /// method exists to prevent could only be measured by hand and never
    /// pinned.
    /// </remarks>
    private static Type[] JudgedTypesOf(IReadOnlyCollection<Type> universe, Type[] roots)
    {
        HashSet<Type> judged = [.. roots];
        Queue<Type> pending = new(roots);

        while (pending.Count > 0)
        {
            foreach (Type next in CarriedContractTypes(pending.Dequeue(), universe))
            {
                if (judged.Add(next))
                    pending.Enqueue(next);
            }
        }

        return [.. judged];
    }

    private static IEnumerable<Type> CarriedContractTypes(
        Type type,
        IReadOnlyCollection<Type> universe) =>
        type
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .SelectMany(p => MembersOfUniverse(p.PropertyType, universe));

    /// <summary>
    /// Every type of the universe a member's declared type reaches — itself,
    /// an array's element type, or <b>any</b> of a generic's arguments.
    /// </summary>
    /// <remarks>
    /// <b>All the arguments, not the single one.</b> An earlier revision
    /// unwrapped a generic only when it had exactly one argument, which is the
    /// shape of every collection this platform uses today and therefore looked
    /// complete. A member typed
    /// <c>IReadOnlyDictionary&lt;string, SomePayload&gt;</c> would have left
    /// <c>SomePayload</c> outside the closure, so a subject inside it would
    /// have travelled on the command with the gate silently green — the same
    /// false negative the shared-payload case produced, reached through the
    /// type system rather than through the definition.
    /// </remarks>
    private static IEnumerable<Type> MembersOfUniverse(
        Type type,
        IReadOnlyCollection<Type> universe)
    {
        if (universe.Contains(type))
            yield return type;

        if (type.IsArray && type.GetElementType() is Type element)
        {
            foreach (Type reached in MembersOfUniverse(element, universe))
                yield return reached;
        }

        if (!type.IsGenericType)
            yield break;

        foreach (Type argument in type.GetGenericArguments())
        {
            foreach (Type reached in MembersOfUniverse(argument, universe))
                yield return reached;
        }
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
        // ADR-028 settled it (#63). The subject of a money-movement decision is
        // the deciding service's to derive from its own record, so a subject
        // identifier on a command transports an authority the receiver already
        // holds — a second source for a decision that must have exactly one.
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
            "the subject of a money-movement decision is the deciding service's to derive " +
            "from its own record, so a subject here transports an authority the receiver " +
            "already holds — a second source for a decision that must have exactly one " +
            "(ADR-028): " +
            string.Join(", ", offenders.Select(o => $"{o.Command}.{o.Member}")));
    }

    /// <summary>
    /// Every member the judged commands are approved to carry. Not a
    /// description of them — a gate: a name absent from here fails the build.
    /// </summary>
    /// <remarks>
    /// <b>The subject rule is a deny-list and this is the allow-list beside
    /// it.</b> <see cref="SubjectSpellings"/> rejects six substrings, so
    /// <c>OwnerId</c> or <c>AccountHolderId</c> walks past it — the failure
    /// mode <c>docs/lessons.md</c> records against the Grok verdict check, which
    /// refused a list of terminal states and passed every state nobody had
    /// thought of. Enumerating what is acceptable is what closed that one.
    /// <para>
    /// <b>What this buys is a forced decision, not a verdict.</b> It cannot
    /// tell whether a new member is a subject; it makes adding one impossible
    /// to do silently, which is the scaffold's rule — a tool that refuses
    /// input it has never been shown beats one that guesses. So the escape
    /// narrows from "any spelling nobody predicted" to "a spelling somebody
    /// approved into this list", and that somebody is a reviewer looking at a
    /// red build rather than a reader who might notice.
    /// </para>
    /// </remarks>
    private static readonly (Type Contract, string Member)[] ApprovedCommandMembers =
    [
        (typeof(AuthorisePayment), "OrderId"),
        (typeof(AuthorisePayment), "Amount"),      // instruction, not authority
        (typeof(AuthorisePayment), "Currency"),    // the same
        (typeof(CancelOrder), "OrderId"),
        (typeof(CancelOrder), "Reason"),
        (typeof(ConfirmOrder), "OrderId"),
        (typeof(ConfirmOrder), "PaymentReference"),
        (typeof(MarkOrderShipped), "OrderId"),
        (typeof(MarkOrderShipped), "TrackingNumber"),
        (typeof(FlagOrderForReview), "OrderId"),
        (typeof(FlagOrderForReview), "Reason"),
        (typeof(ReserveStock), "OrderId"),
        (typeof(ReserveStock), "Lines"),
        (typeof(ReleaseStock), "OrderId"),
        (typeof(StockLine), "ProductId"),
        (typeof(StockLine), "Quantity")
    ];

    [Fact]
    public void No_command_contract_carries_an_unapproved_member()
    {
        // The allow-list half of ADR-028's rule. The subject test above is a
        // deny-list of six substrings, so a subject spelled `OwnerId` reaches
        // Payments with every assertion green — and no list of spellings can
        // be complete, which its own remarks say. This one fails on any member
        // nobody has approved, whatever it is called.
        //
        // It does not decide whether the new member is a subject. It makes the
        // question unavoidable: the build goes red, and the fix is a line in
        // ApprovedCommandMembers written by somebody who had to think about
        // it. ADR-028 and §12 both state the rule that way rather than as
        // mechanically settled, because it is not.
        // **Scoped to the contract that approved it, not to the name.** A flat
        // list of names lets an approval leak: `PaymentReference` approved for
        // ConfirmOrder would silently permit it on AuthorisePayment, and a new
        // command assembled entirely from names already in use would pass
        // without anyone adding a line — the forced review never happening,
        // which is the only thing this gate was for.
        (string Command, string Member)[] unapproved =
        [
            .. Commands
                .SelectMany(t => t
                    .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(p => !ApprovedCommandMembers.Contains((t, p.Name)))
                    .Select(p => (t.FullName!, p.Name)))
        ];

        unapproved.ShouldBeEmpty(
            "a member on a judged command is a decision under ADR-028, so it is approved " +
            "explicitly or it is not there: " +
            string.Join(", ", unapproved.Select(u => $"{u.Command}.{u.Member}")));
    }

    [Fact]
    public void The_approved_member_list_holds_nothing_the_commands_have_dropped()
    {
        // The other direction, and the reason it is not optional: an entry
        // left behind by a removed member is a name pre-approved for whatever
        // arrives under it next, which is a deny-list hole reintroduced inside
        // the allow-list that replaced one.
        (Type Contract, string Member)[] live =
        [
            .. Commands
                .SelectMany(t => t
                    .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Select(p => (t, p.Name)))
        ];

        ApprovedCommandMembers.ShouldBeSubsetOf(
            live,
            "an approved pair no command carries is a seat reserved for the next member " +
            "to take without review");
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

    /// <summary>
    /// The declared vocabulary, as theory cases. Reading
    /// <see cref="SubjectSpellings"/> rather than restating it is the whole
    /// point — see the remarks on the test below.
    /// </summary>
    public static TheoryData<string> DeclaredSpellings => new(SubjectSpellings);

    [Theory]
    [MemberData(nameof(DeclaredSpellings))]
    public void Every_declared_subject_spelling_is_detected(string spelling)
    {
        // **The control above exercises one entry of six**, so removing or
        // misspelling any of the other five left every assertion green — most
        // of this gate's declared vocabulary unobserved, which is the coverage
        // failure it exists to prevent, inside the control written to prevent
        // it. A probe carries one member per spelling and each is asserted by
        // name, so a vocabulary entry cannot be lost silently.
        //
        // **The cases are generated from the list, because a second copy of it
        // reopened the same hole one layer up.** This was an `InlineData` row
        // per spelling with `SubjectSpellings.ShouldContain(spelling)` beside
        // it, and a comment claiming that pairing failed "in either
        // direction". It did not: a spelling added to the list WITH a probe
        // member but WITHOUT its row satisfied that assertion vacuously and
        // the size check exactly, so the new entry was never exercised and
        // nothing said so. A generated case cannot be forgotten, which is the
        // same argument the saga's publish barrier won over per-test
        // discipline — leave nothing to remember.
        string[] found =
        [
            .. SubjectMembers(typeof(SubjectGateProbes.EverySpelling)).Select(p => p.Name)
        ];

        found.ShouldContain(
            name => name.Contains(spelling, StringComparison.OrdinalIgnoreCase),
            $"the probe declares a member spelled '{spelling}' and the detector must see it");
    }

    [Fact]
    public void The_spelling_vocabulary_and_its_controls_stay_the_same_size()
    {
        // The other direction of the same pairing: a spelling ADDED to the
        // list without a probe member would be a vocabulary entry the theory
        // above generates a case for and nothing can satisfy — informative,
        // but only once the probe is the thing being compared. This pins the
        // probe to the list so a spelling REMOVED from the list cannot leave a
        // stranded member behind, which the theory alone never sees: it
        // enumerates the list, so an entry that has gone takes its case with
        // it.
        SubjectSpellings.Length.ShouldBe(
            typeof(SubjectGateProbes.EverySpelling)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Length,
            "every declared spelling needs a probe member, or it is unobserved");
    }

    [Fact]
    public void The_set_the_subject_gate_reads_holds_the_real_commands()
    {
        // The other half of the same argument, one level up: the control above
        // proves the detector works and says nothing about what it is pointed
        // at. Commands is a closure over CommandRoots, and a discovery that
        // finds no roots makes No_command_contract_carries_a_subject vacuous
        // while leaving it green.
        //
        // **Not merely non-empty, and the difference is StockLine.** Commands
        // legitimately holds a payload a command carries, so a ShouldNotBeEmpty
        // here would still pass with every command root lost. Name the roots
        // the rule exists for instead.
        //
        // **All seven of them, which is §3.2's Accepts columns read across.**
        // An earlier revision named three and would have stayed green while
        // discovery silently dropped the other four — the coverage failure
        // this repository keeps rediscovering, reproduced inside the control
        // written to prevent it.
        foreach (Type root in DeclaredCommandRoots)
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

    [Fact]
    public void Inferred_command_roots_and_the_declared_list_agree()
    {
        // The declared list is what the gate judges, so on its own it is a list
        // nothing checks — which is the drift this repository closes by
        // declaring once and asserting the copies match. Inference is the other
        // copy, and pairing them catches both failures it has.
        //
        // A command added to the contracts and not declared here shows up as an
        // inferred root nobody listed: unjudged today, red now, and the fix is
        // a line somebody had to write.
        //
        // A command REMOVED from inference is the subtler one and the reason
        // this test exists at all. RootsOf calls a non-event a root when
        // nothing else carries it, so an EVENT declaring a property of a
        // command's type takes that command out of the roots — and nothing then
        // reaches it, because only events do. Judging from the declared list
        // means the gate does not lose it; this assertion means nobody loses
        // the fact that it happened.
        Type[] inferred = RootsOf(Contracts);

        inferred.ShouldBe(DeclaredCommandRoots, ignoreOrder: true);
    }

    [Fact]
    public void Every_non_event_contract_is_declared_a_command_or_a_payload()
    {
        // **The pairing above has one blind spot and this is it.** A contract
        // that is BOTH carried only by an event AND absent from
        // DeclaredCommandRoots drops out of the inferred roots and the declared
        // roots alike, so that equality holds while no gate ever inspects the
        // type. Each half of the pairing sees one of those mistakes; neither
        // sees them together, which is the case a real new command most easily
        // arrives in.
        //
        // No structural test can decide whether such a type is dispatched — the
        // type system does not hold that fact, §9.1 defines a command by what
        // it does not implement, and a positive marker only relocates the
        // forgetting to the marker. What IS decidable is whether a human has
        // classified it. Anything in neither list fails here, so a contract
        // cannot enter the assembly unlooked-at.
        Type[] classified = [.. DeclaredCommandRoots, .. DeclaredPayloads];

        Type[] unclassified =
        [
            .. Contracts
                .Where(t => !typeof(IIntegrationEvent).IsAssignableFrom(t))
                .Where(t => !classified.Contains(t))
        ];

        unclassified.ShouldBeEmpty(
            "a non-event contract is a command or a payload, and which one is a decision " +
            "ADR-028 needs taken rather than inferred: " +
            string.Join(", ", unclassified.Select(t => t.FullName)));

        // And the other direction, on the allow-list's own argument: a
        // classification for a type that no longer exists is a seat reserved
        // for whatever takes the name next.
        classified.ShouldBeSubsetOf(
            Contracts,
            "a declared command or payload the assembly no longer holds is a stale entry");
    }

    [Fact]
    public void Inference_alone_loses_a_command_an_event_carries()
    {
        // The measurement behind the paragraph above, pinned rather than
        // argued — and the reason the declared list is not ceremony. The probe
        // universe holds an event whose property is a command's type, which is
        // exactly what removes that command from the inferred roots.
        //
        // Asserted as a FAILING inference, not a passing one: this test's
        // subject is the hole, so it must go red on the day inference stops
        // having it. That is the same discipline as the awaiting-signal alert
        // gate — a list of things known to be missing needs something asserting
        // they are still missing.
        Type[] inferred = RootsOf(SubjectGateProbes.EventCarriesCommandUniverse);

        inferred.ShouldNotContain(
            typeof(SubjectGateProbes.CarriedCommand),
            "inference cannot see this command as a root, which is why the roots are declared");

        // And the declared-root path judges it anyway, which is the property
        // the real gate depends on.
        Type[] judged = JudgedTypesOf(
            SubjectGateProbes.EventCarriesCommandUniverse,
            [typeof(SubjectGateProbes.CarriedCommand)]);

        judged.ShouldContain(
            typeof(SubjectGateProbes.CarriedCommand),
            "a declared command root is judged whatever an event happens to carry");
    }

    [Fact]
    public void A_payload_shared_by_a_command_and_an_event_stays_judged()
    {
        // **The regression this gate's definition took four attempts to get
        // right, pinned rather than measured.** The live contracts have no
        // payload shared between a command and an event, so every assertion
        // over them stays green under the rejected "non-events minus the event
        // closure" implementation — which exempted exactly this shape, because
        // an event reached it, and let a subject travel on the command
        // unjudged. Synthetic types are the only way to hold that closed.
        Type[] judged = JudgedTypesOf(SubjectGateProbes.Universe, RootsOf(SubjectGateProbes.Universe));

        judged.ShouldContain(
            typeof(SubjectGateProbes.SharedLine),
            "a command reaches this type, so the command side's rule applies to it — an event " +
            "also reaching it is what the rejected implementation wrongly treated as an exemption");

        // The other direction, in the same universe: an exemption that must
        // survive, or the fix for the false negative would have reinstated the
        // false positive it replaced.
        judged.ShouldNotContain(
            typeof(SubjectGateProbes.EventOnlyLine),
            "no command reaches this type, and an event is permitted a subject");

        // And the gate must actually see the subject once the type is judged,
        // which is the step that turns membership into a build failure.
        SubjectMembers(typeof(SubjectGateProbes.SharedLine))
            .Select(p => p.Name)
            .ShouldContain(nameof(SubjectGateProbes.SharedLine.CustomerId));
    }

    [Fact]
    public void A_payload_reached_through_a_two_argument_generic_is_judged()
    {
        // `ProbeCommand` carries its payload as
        // IReadOnlyDictionary<string, SharedLine>. An earlier revision unwrapped
        // a generic only when it had exactly one argument — true of every
        // collection this platform uses today, which is what made the gap look
        // like completeness — so the value type fell outside the closure and
        // its subject travelled unjudged.
        //
        // The assertion above already fails if this regresses, since SharedLine
        // is reached only through that dictionary. This one names the reason,
        // so a failure reports which of the two defects came back.
        CarriedContractTypes(typeof(SubjectGateProbes.ProbeCommand), SubjectGateProbes.Universe)
            .ShouldContain(
                typeof(SubjectGateProbes.SharedLine),
                "every generic argument is part of the payload graph, not only the single one " +
                "a one-argument collection happens to have");
    }

    private static string Names(IEnumerable<Type> types) =>
        string.Join(", ", types.Select(t => t.FullName));
}
