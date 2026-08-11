using Common.Infrastructure.Outbox;
using Shouldly;
using Xunit;

namespace Common.Infrastructure.Tests;

/// <summary>
/// The persisted name of §9.4. Every assertion here is about a column two
/// deployments read differently, which is why the map exists at all.
/// </summary>
public class MessageTypeMapTests
{
    private static MessageTypeMap Map() => new([typeof(SampleDomainEvent).Assembly]);

    [Fact]
    public void The_persisted_name_carries_no_assembly_and_no_version()
    {
        // The whole point. AssemblyQualifiedName would embed a version a
        // release pipeline bumps automatically, and every row written before
        // the deploy would stop resolving — outbox depth climbing after a
        // release that looked clean.
        string name = Map().NameOf(typeof(SampleDomainEvent));

        name.ShouldBe("Common.Infrastructure.Tests.SampleDomainEvent");
        name.ShouldNotContain("Version=");
        name.ShouldNotContain("Culture=");
    }

    [Fact]
    public void A_name_resolves_back_to_its_type()
    {
        MessageTypeMap map = Map();

        map.Resolve(map.NameOf(typeof(SampleIntegrationEvent))).ShouldBe(typeof(SampleIntegrationEvent));
    }

    [Fact]
    public void Naming_an_unstageable_type_throws()
    {
        // In the transaction, so the command fails rather than the outbox
        // filling with rows nobody can deliver.
        Should
            .Throw<InvalidOperationException>(() => Map().NameOf(typeof(NotAMessage)))
            .Message.ShouldContain(nameof(NotAMessage));
    }

    [Fact]
    public void Resolving_an_unknown_name_throws_and_says_what_to_do()
    {
        // On the dispatcher, where the message that names a departed type is
        // the one that lands in the retry log with its own name in it.
        Should
            .Throw<InvalidOperationException>(() => Map().Resolve("Gone.Away.Event"))
            .Message.ShouldContain("drain the outbox");
    }

    [Fact]
    public void A_value_type_domain_event_is_in_the_map()
    {
        // The scan used to filter on IsClass, which dropped this in silence —
        // a type the rest of the API accepts, absent from the one place that
        // decides whether it can be staged.
        Map().NameOf(typeof(SampleValueTypeDomainEvent))
            .ShouldBe("Common.Infrastructure.Tests.SampleValueTypeDomainEvent");
    }

    [Fact]
    public void A_non_ascii_type_name_round_trips_through_the_map()
    {
        // The premise the varchar column rested on — "a FullName is ASCII by
        // construction" — is false, and this is the type that shows it. The
        // map has always accepted such a name; the column now stores it.
        MessageTypeMap map = Map();

        map.Resolve(map.NameOf(typeof(CommandeCréée))).ShouldBe(typeof(CommandeCréée));
    }

    [Fact]
    public void An_alias_resolves_to_the_type_that_replaced_it()
    {
        // §9.4's rename procedure in one line: both names resolve for one
        // release, so a row staged by an instance that has not been replaced
        // yet is still deliverable by one that has.
        MessageTypeMap map = new(
            [typeof(SampleDomainEvent).Assembly],
            new Dictionary<string, Type> { ["Old.Namespace.SampleDomainEvent"] = typeof(SampleDomainEvent) });

        map.Resolve("Old.Namespace.SampleDomainEvent").ShouldBe(typeof(SampleDomainEvent));

        // Outward it is gone: NameOf keeps writing the current name, which is
        // what lets the old one drain and makes the next release a deletion.
        map.NameOf(typeof(SampleDomainEvent)).ShouldBe("Common.Infrastructure.Tests.SampleDomainEvent");
    }

    [Fact]
    public void The_compatibility_release_writes_the_old_name_and_resolves_both()
    {
        // Release one of §9.4's rename: every instance, replaced or not,
        // resolves both names and writes the one all of them can read. An
        // alias alone would have new instances writing the new name
        // immediately, which the un-replaced ones cannot resolve — the same
        // loss as no alias at all, pointed the other way.
        const string old = "Old.Namespace.SampleDomainEvent";

        MessageTypeMap map = new(
            [typeof(SampleDomainEvent).Assembly],
            new Dictionary<string, Type> { [old] = typeof(SampleDomainEvent) },
            new Dictionary<Type, string> { [typeof(SampleDomainEvent)] = old });

        map.NameOf(typeof(SampleDomainEvent)).ShouldBe(old);
        map.Resolve(old).ShouldBe(typeof(SampleDomainEvent));
        map.Resolve("Common.Infrastructure.Tests.SampleDomainEvent").ShouldBe(typeof(SampleDomainEvent));
    }

    [Fact]
    public void Writing_a_name_the_map_cannot_resolve_fails_the_host()
    {
        // The override without the alias: this instance would stage rows it
        // could not itself deliver.
        Should
            .Throw<InvalidOperationException>(() => new MessageTypeMap(
                [typeof(SampleDomainEvent).Assembly],
                new Dictionary<string, Type>(),
                new Dictionary<Type, string> { [typeof(SampleDomainEvent)] = "Nothing.Resolves.This" }))
            .Message.ShouldContain("cannot resolve");
    }

    [Fact]
    public void Writing_a_name_that_resolves_to_another_type_fails_the_host()
    {
        // The worst thing this class can do, and the only guard whose failure
        // has no symptom: the row is written, claimed and delivered, and the
        // payload is read back as a type it never was.
        Should
            .Throw<InvalidOperationException>(() => new MessageTypeMap(
                [typeof(SampleDomainEvent).Assembly],
                new Dictionary<string, Type>(),
                new Dictionary<Type, string>
                {
                    [typeof(SampleDomainEvent)] = typeof(SampleValueTypeDomainEvent).FullName!
                }))
            .Message.ShouldContain("resolves to");
    }

    [Fact]
    public void An_alias_longer_than_the_column_fails_the_host()
    {
        // Every other name is derived from a type and checked above; an alias
        // is typed by hand, so it is the one that can exceed the column.
        Should
            .Throw<InvalidOperationException>(() => new MessageTypeMap(
                [typeof(SampleDomainEvent).Assembly],
                new Dictionary<string, Type>
                {
                    [new string('n', MessageTypeMap.MaxNameLength + 1)] = typeof(SampleDomainEvent)
                }))
            .Message.ShouldContain("No row can carry it");
    }

    [Fact]
    public void An_alias_onto_a_type_the_map_does_not_carry_fails_the_host()
    {
        // The dispatcher trusts the row's Lane rather than re-deriving it, so
        // an alias onto something Stage would refuse is a second door into the
        // leak the lane guards close.
        Should
            .Throw<InvalidOperationException>(() => new MessageTypeMap(
                [typeof(SampleDomainEvent).Assembly],
                new Dictionary<string, Type> { ["Some.Old.Name"] = typeof(NotAMessage) }))
            .Message.ShouldContain("does not carry");
    }

    [Fact]
    public void An_alias_that_shadows_a_live_name_fails_the_host()
    {
        // Two types would answer to one name and which resolves is not
        // decidable — the duplicate-name argument, one indirection over.
        Should
            .Throw<InvalidOperationException>(() => new MessageTypeMap(
                [typeof(SampleDomainEvent).Assembly],
                new Dictionary<string, Type>
                {
                    ["Common.Infrastructure.Tests.SampleDomainEvent"] = typeof(SampleIntegrationEvent)
                }))
            .Message.ShouldContain("also a live type name");
    }

    [Fact]
    public void An_assembly_listed_twice_fails_the_host()
    {
        // The realistic way two entries collide, and the reason
        // MessageTypeSource.Add exists at all: a test host adds an assembly
        // the production registration already named, and every type in it now
        // appears twice. Being a singleton built at startup, this is a host
        // that will not start rather than a message that resolves to whichever
        // entry won the dictionary.
        //
        // It is the same guard a genuine namespace collision between two
        // assemblies trips, and it is reachable without emitting one.
        Should
            .Throw<InvalidOperationException>(() =>
                new MessageTypeMap([typeof(SampleDomainEvent).Assembly, typeof(SampleDomainEvent).Assembly]))
            .Message.ShouldContain("cannot distinguish");
    }

    [Fact]
    public void Stageable_domain_events_are_the_domain_events_and_not_the_contracts()
    {
        // §12.4 round-trips this set. A contract in it would be checked twice
        // and a domain event missing from it would never be checked at all —
        // and the local lane is the one carrying types §5.5 calls free to
        // change with the code.
        IEnumerable<Type> stageable = Map().StageableDomainEvents;

        stageable.ShouldContain(typeof(SampleDomainEvent));
        stageable.ShouldNotContain(typeof(SampleIntegrationEvent));
    }
}
