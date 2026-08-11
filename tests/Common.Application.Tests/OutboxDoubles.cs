using Common.Domain;

namespace Common.Application.Tests;

/// <summary>Two domain events, so a test can say which lane each took.</summary>
public sealed record Mapped(DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record Unmapped(DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>The contract <see cref="Mapped"/> translates into.</summary>
public sealed record MappedContract(Guid Id);

/// <summary>Hands the dispatcher whatever a test arranged, once.</summary>
public sealed class FakeDomainEventCollector(params IDomainEvent[] events) : IDomainEventCollector
{
    private IDomainEvent[] _events = events;

    /// <summary>How many times the dispatcher asked. §7.5 says exactly once.</summary>
    public int Collections { get; private set; }

    public IReadOnlyList<IDomainEvent> CollectAndClear()
    {
        Collections++;

        // Cleared on the way out, like the real one — a collector that keeps
        // returning the same events would hide a dispatcher that called it
        // twice, which is the defect §7.5's "clears them" clause prevents.
        IDomainEvent[] collected = _events;
        _events = [];
        return collected;
    }
}

/// <summary>
/// The §9.3 allow-list, one entry. <see cref="Throws"/> makes it the other
/// half of that section's table: a registered mapper that fails must fail the
/// command.
/// </summary>
public sealed class FakeIntegrationEventMapper : IIntegrationEventMapper
{
    public bool Throws { get; set; }

    public IReadOnlyList<object> Map(IReadOnlyList<IDomainEvent> domainEvents)
    {
        if (Throws && domainEvents.OfType<Mapped>().Any())
            throw new InvalidOperationException("the mapper refused");

        return [.. domainEvents.OfType<Mapped>().Select(_ => new MappedContract(Guid.CreateVersion7()))];
    }
}

/// <summary>Records what was staged, and on which lane.</summary>
public sealed class FakeIntegrationEventPublisher : IIntegrationEventPublisher
{
    private readonly List<(object Message, OutboxLane Lane)> _staged = [];

    public IReadOnlyList<(object Message, OutboxLane Lane)> Staged => _staged;

    public Task StageAsync(object message, OutboxLane lane, CancellationToken ct)
    {
        _staged.Add((message, lane));
        return Task.CompletedTask;
    }
}

/// <summary>Registered for <see cref="Mapped"/> and for nothing else.</summary>
public sealed class MappedProjection : IProjectionHandler<Mapped>
{
    public Task HandleAsync(Mapped domainEvent, CancellationToken ct) => Task.CompletedTask;
}
