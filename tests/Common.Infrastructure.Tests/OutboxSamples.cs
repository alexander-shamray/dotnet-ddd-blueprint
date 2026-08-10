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

/// <summary>Neither, so <c>NameOf</c> must refuse it.</summary>
public sealed record NotAMessage(string Note);
