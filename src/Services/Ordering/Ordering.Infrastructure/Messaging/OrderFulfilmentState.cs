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
    /// because eight call sites read better as <c>ctx.Saga.OrderId</c> than as
    /// <c>ctx.Saga.CorrelationId</c>. Assigned once in <c>Initially</c>; never
    /// written again.
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
    public Guid? StockTimeoutTokenId { get; set; }

    public Guid? PaymentTimeoutTokenId { get; set; }

    public Guid? DespatchTimeoutTokenId { get; set; }

    public Guid? ReleaseTimeoutTokenId { get; set; }
}
