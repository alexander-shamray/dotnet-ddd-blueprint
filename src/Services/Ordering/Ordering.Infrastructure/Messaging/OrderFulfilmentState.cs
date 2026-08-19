using MassTransit;

namespace Ordering.Infrastructure.Messaging;

/// <summary>
/// §9.6's saga instance. It carries only what the transitions need — every
/// field in that chapter's state machine, and nothing else.
/// </summary>
public sealed class OrderFulfilmentState : SagaStateMachineInstance
{
    /// <summary>
    /// MassTransit correlates on this. <c>CorrelateById(m =&gt; m.Message.OrderId)</c>
    /// in §9.6 means it always holds the order's id.
    /// </summary>
    public Guid CorrelationId { get; set; }

    public string CurrentState { get; set; } = null!;

    /// <summary>
    /// Same value as <see cref="CorrelationId"/>, kept as a named property
    /// because the transitions read better as <c>ctx.Saga.OrderId</c> than as
    /// <c>ctx.Saga.CorrelationId</c>. Assigned once in <c>Initially</c>; never
    /// written again.
    /// <para>
    /// <b>No count here on purpose.</b> §9.6 said "eight call sites" and the
    /// saga has seventeen reads — a number that was wrong before the machine
    /// was finished, and one every new transition falsifies again.
    /// </para>
    /// </summary>
    public Guid OrderId { get; set; }

    public Guid CustomerId { get; set; }

    public decimal Total { get; set; }

    public string Currency { get; set; } = null!;

    public DateTimeOffset StartedAt { get; set; }

    /// <summary>
    /// Set on entry to <c>Compensating</c>, read by both exits from it.
    /// <c>null!</c> like <see cref="CurrentState"/> and <see cref="Currency"/>
    /// above: the state machine guarantees it is written before any transition
    /// reads it, so the property is not nullable even though the column is — a
    /// saga that never compensates stores NULL, and that is a fact about the
    /// row rather than a case the code handles.
    /// </summary>
    public string CancelReason { get; set; } = null!;

    // One token per schedule — Unschedule needs the specific token, so two
    // waits cannot share a field.
    //
    // **On ADR-021's scheduler these are written and never read back**, and
    // the honest reason is worth carrying beside them: MassTransit 8.5.3's
    // DelayedScheduleMessageProvider.CancelScheduledSend returns
    // Task.CompletedTask on both overloads (checked against the tagged
    // source), so a delayed message cannot be recalled once the broker holds
    // it. Every Unschedule in the saga is therefore a no-op, and every order
    // keeps its timeouts until they fire.
    //
    // They stay because they are the scheduler's contract rather than this
    // saga's convenience — a scheduler that DOES cancel needs them, and ADR-021
    // names Quartz as its own successor. What carries correctness meanwhile is
    // the state machine: a timeout arriving in a state that does not handle it
    // is ignored, and one arriving after finalisation is discarded. Both
    // measured, and the first has a test.
    public Guid? StockTimeoutTokenId { get; set; }

    public Guid? PaymentTimeoutTokenId { get; set; }

    public Guid? DespatchTimeoutTokenId { get; set; }

    public Guid? ReleaseTimeoutTokenId { get; set; }
}
