using Common.Application;
using Ordering.Domain.Orders;

namespace Ordering.Application.Orders.ConfirmOrder;

/// <summary>
/// Record that payment for an order was authorised (§9.6). Sent by the
/// fulfilment saga on <c>PaymentAuthorised</c> and reachable no other way.
/// </summary>
/// <remarks>
/// <b>No <c>CommandOrigin</c>, unlike <c>CancelOrderCommand</c>, and the
/// difference is that there is no second way in.</b> That command carries an
/// origin because a customer may also cancel over HTTP, so the ownership check
/// of §11.4 has two callers to tell apart. This one has a single ingress — the
/// saga, over <c>ordering-commands</c> — and **no HTTP route maps it**, which is
/// the distinction that matters rather than "no endpoint": it very much has a
/// receive endpoint, and `ConfirmOrderMapper` is what binds it there. Adding
/// the field would state a difference the code cannot make, and a
/// discriminator with one value is the kind of thing a later reader completes
/// by adding the second ingress it implies.
/// <para>
/// <see cref="Reference"/> is the domain type rather than the wire string, and
/// the parse happens in the mapper for the reason §9.4 gives about
/// <c>CancelOrder.Reason</c>: a reference this service cannot construct is a
/// malformed contract, which belongs in the error queue on the first attempt
/// rather than being retried five times and then acked as a domain rejection.
/// </para>
/// </remarks>
public sealed record ConfirmOrderCommand(Guid OrderId, PaymentReference Reference) : ICommand<Result>;
