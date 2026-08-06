using Shouldly;
using Xunit;

namespace Common.Domain.Tests;

public class AggregateRootTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Raising_an_event_records_it_on_the_aggregate()
    {
        var aggregate = new TestAggregate(TestId.New());

        aggregate.RecordThat(new TestDomainEvent("something happened", Now));

        TestDomainEvent recorded = aggregate.DomainEvents
            .OfType<TestDomainEvent>()
            .ShouldHaveSingleItem();

        recorded.Name.ShouldBe("something happened");
    }

    [Fact]
    public void A_new_aggregate_has_raised_nothing()
    {
        var aggregate = new TestAggregate(TestId.New());

        aggregate.DomainEvents.ShouldBeEmpty();
    }

    [Fact]
    public void Events_are_kept_in_the_order_they_were_raised()
    {
        var aggregate = new TestAggregate(TestId.New());

        aggregate.RecordThat(new TestDomainEvent("first", Now));
        aggregate.RecordThat(new TestDomainEvent("second", Now.AddSeconds(1)));

        aggregate.DomainEvents
            .OfType<TestDomainEvent>()
            .Select(e => e.Name)
            .ShouldBe(["first", "second"]);
    }

    [Fact]
    public void Clearing_removes_every_recorded_event()
    {
        var aggregate = new TestAggregate(TestId.New());
        aggregate.RecordThat(new TestDomainEvent("something happened", Now));

        aggregate.ClearDomainEvents();

        aggregate.DomainEvents.ShouldBeEmpty();
    }

    [Fact]
    public void DomainEvents_cannot_be_mutated_through_the_property()
    {
        var aggregate = new TestAggregate(TestId.New());

        // The property returns a read-only view, not the backing list. Handing
        // out the list itself would let a caller stage an event the aggregate
        // never raised, past every invariant on it.
        Action act = () =>
            ((ICollection<IDomainEvent>)aggregate.DomainEvents).Add(new TestDomainEvent("smuggled", Now));

        act.ShouldThrow<NotSupportedException>();
    }

    // The two markers of §5.5, tested through the queries that use them. Both
    // failures are silent: a change tracker filtered by a marker the base class
    // does not carry matches nothing, stages no outbox rows, and commits.
    [Fact]
    public void An_aggregate_root_is_reachable_as_IHasDomainEvents()
    {
        object[] tracked = [new TestAggregate(TestId.New())];

        tracked.OfType<IHasDomainEvents>().ShouldHaveSingleItem();
    }

    [Fact]
    public void An_aggregate_root_is_reachable_as_IAggregateRoot()
    {
        object[] tracked = [new TestAggregate(TestId.New())];

        tracked.OfType<IAggregateRoot>().ShouldHaveSingleItem();
    }

    [Fact]
    public void An_aggregate_root_is_an_entity()
    {
        var id = TestId.New();

        var one = new TestAggregate(id);
        var other = new TestAggregate(id);

        one.Equals(other).ShouldBeTrue();
    }

    [Fact]
    public void A_new_aggregate_carries_an_empty_concurrency_token()
    {
        var aggregate = new TestAggregate(TestId.New());

        // Never null: the token is a SQL Server rowversion, and EF writes it
        // back on save. A null here would fault the first update rather than
        // the first insert, which is the harder failure to attribute.
        aggregate.Version.ShouldBeEmpty();
    }
}
