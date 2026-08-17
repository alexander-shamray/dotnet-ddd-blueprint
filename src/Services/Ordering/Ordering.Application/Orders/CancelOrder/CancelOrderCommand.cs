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
/// No <c>CommandId</c> and no <c>IIdempotentCommand</c> either, for the reason
/// <c>PublishProductCommand</c> carries none: §6.4 warns that the field
/// without the behaviour is unprotected, and <c>IdempotencyBehavior</c> (§8.5)
/// does not exist yet. Both join in the PR that builds it.
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
