using Common.Contracts;
using Common.Domain;

namespace Common.Infrastructure.Tests;

/// <summary>
/// One of each kind the map admits, declared here so the map built over this
/// assembly has something real to find. The names matter as much as the
/// shapes: §9.4 persists <c>FullName</c>, so these types are what the
/// round-trip and resolve tests below assert against.
/// </summary>
public sealed record SampleIntegrationEvent : IIntegrationEvent
{
    public required Guid MessageId { get; init; }

    public required Guid CorrelationId { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public required string Note { get; init; }
}

public sealed record SampleDomainEvent(DateTimeOffset OccurredAt, string Note) : IDomainEvent;

/// <summary>
/// A domain event that is a value type. Neither event interface carries a
/// class constraint, so this compiles, raises and dispatches like any other —
/// and the map has to hold it, or staging fails inside the transaction for a
/// type the rest of the API accepted.
/// </summary>
public readonly record struct SampleValueTypeDomainEvent(DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>
/// A domain event whose name is not ASCII. C# permits Unicode identifiers, so
/// this is a legal event in a codebase whose domain language is not English —
/// and the map accepts it, which is why <c>MessageType</c> is nvarchar: under
/// varchar the database code page would mangle the persisted name and
/// <c>Resolve</c> would fail on a type that was never wrong.
/// </summary>
public sealed record CommandeCréée(DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>
/// Both at once, which §5.5 calls the most consequential mistake in this
/// architecture — and which C# is perfectly happy to compile, so `Stage` is
/// what has to refuse it.
/// </summary>
public sealed record Conflated : IDomainEvent, IIntegrationEvent
{
    public required Guid MessageId { get; init; }

    public required Guid CorrelationId { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }
}

/// <summary>Neither, so <c>NameOf</c> must refuse it.</summary>
public sealed record NotAMessage(string Note);
