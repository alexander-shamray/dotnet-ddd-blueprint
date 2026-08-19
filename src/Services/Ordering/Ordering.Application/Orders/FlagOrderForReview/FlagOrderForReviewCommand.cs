using Common.Application;

namespace Ordering.Application.Orders.FlagOrderForReview;

/// <summary>
/// Escalate an order to a human (§9.6) — the path for a wait with no automatic
/// compensation.
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
