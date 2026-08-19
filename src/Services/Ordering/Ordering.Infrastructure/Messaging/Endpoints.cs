namespace Ordering.Infrastructure.Messaging;

/// <summary>
/// The queues §9.6's saga sends to, declared once so the state machine reads as
/// intent rather than as string handling.
/// </summary>
/// <remarks>
/// <c>queue:</c> is MassTransit's short-address form, resolved against the
/// configured transport. Names must match the <c>ReceiveEndpoint</c>
/// declarations in each owning service — a command sent to an undeclared queue
/// is not an error, it is silence (§9.4).
/// <para>
/// The alternative is <c>EndpointConvention.Map&lt;ReserveStock&gt;(…)</c> at
/// startup, which lets activities call <c>.Send(ctx =&gt; …)</c> with no
/// address. It reads more cleanly and fails at runtime rather than compile time
/// if a mapping is missed. Explicit addresses are used here because a blueprint
/// should show where the message goes.
/// </para>
/// <para>
/// <b>Internal, unlike <c>CatalogEventsQueue</c> beside it</b>, and the two are
/// answering different questions: this holds addresses the saga <em>sends</em>
/// to and nothing outside this assembly sends, while that one is the name a
/// queue is <em>declared</em> under — which is what an inbox row records and
/// therefore what a test has to be able to name.
/// </para>
/// </remarks>
internal static class Endpoints
{
    public static readonly Uri InventoryQueue = new("queue:inventory-commands");

    public static readonly Uri PaymentsQueue = new("queue:payments-commands");

    /// <summary>
    /// This service's own command queue. The saga sends to it over the broker
    /// rather than dispatching in-process, and that is the design rather than
    /// an accident of where the class sits: §9.6's saga coordinates by message
    /// only, so a restart between two of its steps loses nothing, and the
    /// command arrives through the same inbox, retry and transaction pipeline
    /// whether Ordering or a peer produced it.
    /// </summary>
    public static readonly Uri OrderingQueue = new("queue:ordering-commands");
}
