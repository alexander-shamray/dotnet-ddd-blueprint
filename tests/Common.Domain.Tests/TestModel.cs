namespace Common.Domain.Tests;

// The smallest model that exercises the building blocks: the typed-ID pattern
// of §5.2, two entity types over the same ID, an aggregate root and a domain
// event.
//
// Deliberately anonymous. A `TestOrder` here would pull §5's Ordering sample
// into the one project that must keep compiling whatever domain the platform
// settles on, and these types exist to test Common.Domain rather than to
// illustrate a business.

/// <summary>
/// A strongly typed identifier in the form §5.2 specifies — a readonly record
/// struct over a version-7 <see cref="Guid"/>, with a factory rather than a
/// constructor call at every site.
/// </summary>
internal readonly record struct TestId(Guid Value)
{
    public static TestId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}

/// <summary>
/// A second identifier type over the same underlying primitive. Its only job is
/// to be the argument that must not compile where a <see cref="TestId"/> is
/// expected — the whole point of §5.2.
/// </summary>
internal readonly record struct OtherTestId(Guid Value)
{
    public static OtherTestId New() => new(Guid.CreateVersion7());
}

/// <summary>
/// An entity with a field that takes no part in identity, so a test can state
/// §5.1's rule: same ID, same thing, regardless of everything else.
/// </summary>
internal sealed class TestEntity : Entity<TestId>
{
    public TestEntity(TestId id, string label)
    {
        Id = id;
        Label = label;
    }

    public string Label { get; }
}

/// <summary>
/// A different entity type over the same ID type. Two of these compare equal to
/// each other and never to a <see cref="TestEntity"/> holding the same ID.
/// </summary>
internal sealed class OtherTestEntity : Entity<TestId>
{
    public OtherTestEntity(TestId id) => Id = id;
}

/// <summary>
/// An aggregate root that exposes <c>Raise</c> through a named operation, the
/// way a real aggregate does — the base method is protected, and a test that
/// called it directly would be testing a member no caller can reach.
/// </summary>
internal sealed class TestAggregate : AggregateRoot<TestId>
{
    public TestAggregate(TestId id) => Id = id;

    public void RecordThat(IDomainEvent domainEvent) => Raise(domainEvent);
}

internal sealed record TestDomainEvent(string Name, DateTimeOffset OccurredAt) : IDomainEvent;
