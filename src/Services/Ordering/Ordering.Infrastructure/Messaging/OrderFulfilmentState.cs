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

    /// <summary>
    /// An <c>AuthorisePayment</c> has been sent and no verdict has come back.
    /// Set where that command is sent, cleared by the first
    /// <c>PaymentAuthorised</c> or <c>PaymentDeclined</c> to arrive in any
    /// state.
    /// </summary>
    /// <remarks>
    /// <b>A timeout does not clear it, and that is the whole point of the
    /// field.</b> §9.6 waits fifteen minutes for a verdict and then
    /// compensates, but a slow PSP has not answered — it has merely not
    /// answered <i>yet</i>, and the authorisation it is still holding is
    /// exactly the money
    /// <c>payment_authorised_during_compensation</c> exists to escalate. So
    /// the timeout ends the <i>wait</i> and not the <i>obligation</i>, which
    /// is why the two are separate facts rather than one.
    /// <para>
    /// <b>It cannot be derived from the state, which is why it is stored.</b>
    /// <c>Compensating</c> is reached five ways and the answer differs by
    /// route: from <c>AwaitingStock</c> nothing was ever authorised, from
    /// <c>AwaitingConfirmation</c> the verdict already landed, and from
    /// <c>AwaitingPayment</c> it depends on whether a decline, a timeout or a
    /// cancellation brought it there. A state name carries one of those
    /// answers and the instance has to carry the rest.
    /// </para>
    /// </remarks>
    public bool PaymentVerdictOutstanding { get; set; }

    /// <summary>
    /// The compensation's stock half has come to rest — either
    /// <c>StockReleased</c> arrived or <c>ReleaseTimeout</c> gave up on it and
    /// raised <c>stock_not_released</c>.
    /// </summary>
    /// <remarks>
    /// <b>Read with <see cref="PaymentVerdictOutstanding"/> and never alone.</b>
    /// <c>Compensating</c> finalises when both halves are settled, and either
    /// one may land first, so each exit has to ask about the other rather
    /// than assume it is last
    /// (<a href="https://github.com/alexander-shamray/dotnet-ddd-blueprint/issues/124">#124</a>).
    /// <para>
    /// <b>The release token is not a substitute for it.</b>
    /// <see cref="ReleaseTimeoutTokenId"/> is cleared by <c>Unschedule</c> on
    /// the <c>StockReleased</c> exit and left standing on the timeout's own,
    /// so a null test answers for one settled route and not the other — an
    /// inference over a field kept for a different purpose, which is how it
    /// would come to be read as a general one.
    /// </para>
    /// </remarks>
    public bool StockReleaseSettled { get; set; }

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
    // NOT the state machine, and this comment used to say it was: "a timeout
    // arriving in a state that does not handle it is ignored" names a
    // mechanism the machine does not have. §9.6 keeps MassTransit's default,
    // so an arrival no state accepts FAULTS — the catch-all that would have
    // ignored it was removed for losing a crash and a misroute along with the
    // duplicate (#128).
    //
    // What actually carries it is the TOKEN below. A scheduled message is
    // delivered with the token id its schedule was armed with, and MassTransit
    // discards one that no longer matches this instance before the machine is
    // asked — so a stale timeout never reaches a transition to be ignored or
    // to fault. Unschedule clears the id here even though ADR-021 makes the
    // broker-side cancel a no-op, which is why the no-op is harmless. One
    // arriving after finalisation is discarded by OnMissingInstance instead.
    // Both measured.
    public Guid? StockTimeoutTokenId { get; set; }

    public Guid? PaymentTimeoutTokenId { get; set; }

    public Guid? ConfirmationTimeoutTokenId { get; set; }

    public Guid? DespatchTimeoutTokenId { get; set; }

    public Guid? ReleaseTimeoutTokenId { get; set; }
}
