using Common.Application;
using Ordering.Domain.Orders;

namespace Ordering.Application.Orders.CancelOrder;

/// <summary>
/// Cancel an order. Dispatched from two places — the endpoint, and §9.4's
/// <c>CommandConsumer</c> when the saga compensates — which is why the origin
/// is on the command rather than inferred.
/// </summary>
/// <remarks>
/// No <c>CustomerId</c>, and the omission is the control (§11.4). The subject
/// of a write is bound from the principal and never from the request; here the
/// owner is read off the loaded aggregate, so nothing the caller sends says
/// whose order it is.
/// <para>
/// <b>Still no <c>CommandId</c> and no <c>IIdempotentCommand</c>, and that is
/// now a decision rather than a wait.</b> The PR that built §8.5's behaviour
/// opted <c>PlaceOrderCommand</c> and <c>PublishProductCommand</c> in and left
/// this one out, on three grounds that the two ingresses above make specific
/// to it:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>There is nothing to duplicate.</b> <c>Order.Cancel</c> returns early on
/// an order already <c>Cancelled</c> (§5.4) — the aggregate is idempotent, so
/// a second dispatch changes no state and raises no second event. §8.5 buys
/// protection where a retry creates a second <em>thing</em>; here it would buy
/// a replayed <c>Result</c>.
/// </description></item>
/// <item><description>
/// <b>The broker ingress has no subject to claim under.</b>
/// <see cref="ICurrentUser.IsAuthenticated"/> is false for a message-borne
/// command, so every saga-sent cancellation would claim under the shared
/// <c>"system"</c> segment — §8.5's largest named residual, and this command
/// is the one place in the solution that would walk straight into it.
/// </description></item>
/// <item><description>
/// <b>Duplicates on that ingress are already absorbed one layer down.</b>
/// §9.5's inbox suppresses a redelivered <c>CancelOrder</c> before the
/// dispatcher sees it, so the behaviour would be a second answer to a question
/// already answered.
/// </description></item>
/// </list>
/// <para>
/// Opting in is a decision and forgetting to is not meant to look like one
/// (§8.5) — so the argument lives here, where the next reader of this record
/// finds it, rather than in the PR that took it.
/// </para>
/// </remarks>
public sealed record CancelOrderCommand(
    Guid OrderId,
    CancellationReason Reason,
    CommandOrigin InitiatedBy) : ICommand<Result>
{
    /// <summary>
    /// The trusted path, stated rather than inferred. §11.4's callout is the
    /// argument for making it a positive claim: an earlier version of the
    /// ownership check treated the <em>absence</em> of a principal as proof
    /// the saga had sent the command, which is a condition an attacker
    /// arranges rather than avoids.
    /// </summary>
    public bool IsSystemInitiated => InitiatedBy is CommandOrigin.System;
}

/// <summary>
/// Who asked. Written as a literal at each entry point — never bound from a
/// request or a message, because a value a caller could set is a value that
/// skips the ownership check.
/// </summary>
public enum CommandOrigin
{
    /// <summary>
    /// An HTTP request with a principal behind it. The ownership check
    /// applies.
    /// </summary>
    /// <remarks>
    /// <b>The zero value, deliberately (Appendix D).</b> An origin nobody set
    /// therefore fails closed: it means "check the owner", which is the
    /// answer that refuses rather than the one that admits. Declaring
    /// <c>System</c> first, or starting the members at 1, would both make the
    /// safe default an accident of declaration order.
    /// </remarks>
    User,

    /// <summary>
    /// §9.6's saga compensating, over the broker, with no principal at all.
    /// The check is skipped because the decision was already authorised at the
    /// endpoint that started the saga — not because no caller could be found.
    /// </summary>
    System
}
