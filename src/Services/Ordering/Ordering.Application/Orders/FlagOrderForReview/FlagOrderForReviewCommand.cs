using Common.Application;

namespace Ordering.Application.Orders.FlagOrderForReview;

/// <summary>
/// Escalate an order to a human (§9.6) — the path for work this workflow
/// cannot finish itself.
/// <para>
/// <b>Not "a wait with no automatic compensation", which this said.</b> That
/// describes <c>not_despatched</c>, <c>stock_not_released</c> and
/// <c>not_confirmed</c>, and not the other two of
/// <see cref="Common.Contracts.Ordering.V1.ReviewReasons"/>'s codes, which are
/// raised against a workflow ending in cancellation — no wait, and nothing ran
/// out. <b>Named rather than counted</b>: this said "two of four" while there
/// were five, and "the moment an authorisation turns up" while #143 also
/// raises <c>cancelled_after_confirmation</c> on two despatch branches.
/// A caller taught the narrower contract would read a
/// <c>payment_authorised_during_compensation</c> row as a stall.
/// </para>
/// </summary>
/// <remarks>
/// <see cref="Reason"/> stays a string where the other saga commands carry
/// domain types, because there is no domain type behind it. A review reason is
/// a fact about the <em>process</em> rather than about the order, and §9.6 is
/// explicit that this command touches no aggregate. The vocabulary is closed
/// all the same — <c>ReviewReasons</c> — and the mapper is what refuses a code
/// outside it, so the column stays a set §13.6 can alert on.
/// </remarks>
public sealed record FlagOrderForReviewCommand(Guid OrderId, string Reason) : ICommand<Result>;
