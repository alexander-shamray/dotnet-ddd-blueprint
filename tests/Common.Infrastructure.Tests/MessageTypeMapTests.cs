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
