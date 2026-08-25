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

    /// <summary>The universe those four form, in the order a reader expects.</summary>
    internal static Type[] Universe =>
    [
        typeof(ProbeCommand),
        typeof(ProbeEvent),
        typeof(SharedLine),
        typeof(EventOnlyLine)
    ];
}
