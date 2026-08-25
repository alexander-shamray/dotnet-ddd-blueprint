using Common.Contracts;

namespace Platform.IntegrationTests;

/// <summary>
/// Synthetic contracts for the subject gate's own regression cases.
/// </summary>
/// <remarks>
/// <b>In this test assembly and outside <c>Common.Contracts</c>' namespace,
/// deliberately.</b> <c>UnversionedProbe</c> sits in that namespace because the
/// defect it reproduces is a type declared straight into it; these reproduce a
/// defect in the <em>algorithm</em>, so they must not be discoverable as
/// contracts at all. <c>ContractTests.Contracts</c> is built from
/// <c>typeof(OrderPlaced).Assembly</c>, which is not this one, so they are
/// never in it either way — the namespace is the second guard rather than the
/// first.
/// <para>
/// <b>They exist because the real contracts cannot express the case.</b> No
/// payload is shared between a command and an event today, so the false
/// negative that shape produces could only be measured by hand during
/// development — and a defect measured once and reverted is pinned by nothing.
/// Replacing the closure with the rejected "non-events minus the event
/// closure" implementation leaves every assertion over the live contracts
/// green; it fails these.
/// </para>
/// </remarks>
internal static class SubjectGateProbes
{
    /// <summary>A payload carried by a command <b>and</b> by an event.</summary>
    internal sealed record SharedLine(Guid ProductId, Guid CustomerId, int Quantity);

    /// <summary>A payload only the event carries.</summary>
    internal sealed record EventOnlyLine(Guid ProductId, Guid CustomerId);

    /// <summary>
    /// A command reaching its payload through a <b>two-argument</b> generic,
    /// which is the second shape the closure used to miss.
    /// </summary>
    internal sealed record ProbeCommand(
        Guid OrderId,
        IReadOnlyDictionary<string, SharedLine> Lines);

    /// <summary>An event carrying the shared payload and one of its own.</summary>
    internal sealed record ProbeEvent : IIntegrationEvent
    {
        public required Guid MessageId { get; init; }

        public required Guid CorrelationId { get; init; }

        public required DateTimeOffset OccurredAt { get; init; }

        public required IReadOnlyList<SharedLine> Shared { get; init; }

        public required IReadOnlyList<EventOnlyLine> Own { get; init; }
    }

    /// <summary>
    /// One member per declared spelling in <c>SubjectSpellings</c>, so the
    /// detector's whole vocabulary is exercised rather than its first entry.
    /// </summary>
    /// <remarks>
    /// <b>The names are deliberately not all <c>*Id</c>.</b> The list is
    /// matched as a case-insensitive substring, and a probe set that only used
    /// one suffix would leave that part of the predicate unobserved too.
    /// </remarks>
    internal sealed record EverySpelling(
        Guid CustomerId,
        Guid BuyerReference,
        Guid PayerId,
        Guid SubjectIdentifier,
        Guid UserId,
        Guid PrincipalId);

    /// <summary>The universe those four form, in the order a reader expects.</summary>
    internal static Type[] Universe =>
    [
        typeof(ProbeCommand),
        typeof(ProbeEvent),
        typeof(SharedLine),
        typeof(EventOnlyLine)
    ];

    /// <summary>
    /// A command dispatched to its own queue, which an event also happens to
    /// carry. Nothing in the live contracts has this shape, so the hole it
    /// demonstrates could only ever be argued rather than measured.
    /// </summary>
    internal sealed record CarriedCommand(Guid OrderId, Guid CustomerId);

    /// <summary>
    /// The event that swallows it. Root inference asks whether anything in the
    /// universe carries a type, and this does — so <c>CarriedCommand</c> stops
    /// being a root, and nothing reaches it, because only an event does.
    /// </summary>
    internal sealed record CommandCarryingEvent(Guid OrderId, CarriedCommand Echo) : IIntegrationEvent
    {
        public Guid MessageId => Guid.Empty;

        public Guid CorrelationId => Guid.Empty;

        public DateTimeOffset OccurredAt => DateTimeOffset.MinValue;
    }

    /// <summary>The two of them, as a universe the roots test can drive.</summary>
    internal static Type[] EventCarriesCommandUniverse =>
    [
        typeof(CarriedCommand),
        typeof(CommandCarryingEvent)
    ];
}
