namespace Ordering.Infrastructure.Messaging;

/// <summary>
/// The scheduled messages §9.6's saga arms, one per wait state.
/// </summary>
/// <remarks>
/// <b>Not contracts, and deliberately not in <c>Common.Contracts</c>.</b> §3.2
/// lists what each service publishes, consumes and accepts, and none of these
/// appears in any column: they are sent by this saga to itself, they carry no
/// envelope, and no peer may bind them. §4.3 permits exactly one assembly to
/// cross a service boundary and a private timeout is not a candidate for it —
/// putting these there would advertise a set of messages another service could
/// legitimately publish, which is precisely how a saga acquires a way to be
/// driven from outside.
/// <para>
/// One record per wait rather than one <c>FulfilmentExpired(Guid OrderId,
/// string Wait)</c>, because MassTransit correlates a schedule by message
/// <em>type</em>: a single type would make every schedule the same
/// subscription, and the state machine would have no way to tell which wait a
/// given expiry belonged to. (Not "which token <c>Unschedule</c> is
/// cancelling": on ADR-021's scheduler it cancels nothing — see
/// <see cref="OrderFulfilmentState"/>. The types still have to be distinct,
/// because a stale expiry is discriminated by its <em>type</em> against the
/// state it arrives in.)
/// </para>
/// <para>
/// <b>No count here, and this file used to open with one.</b> It said "the
/// four" while the saga armed four, and #126's split made it five in a change
/// that had no reason to look at this line. The invariant a reader wants is
/// one per wait state, which <c>Every_wait_state_declares_a_schedule</c>
/// asserts against the machine — so the rule is written here and the arithmetic
/// is left to the test that can actually do it.
/// </para>
/// </remarks>
public sealed record StockReservationExpired(Guid OrderId);

/// <inheritdoc cref="StockReservationExpired"/>
public sealed record PaymentAuthorisationExpired(Guid OrderId);

/// <summary>
/// The acknowledgement of a <c>ConfirmOrder</c> did not arrive (#126).
/// </summary>
/// <remarks>
/// <inheritdoc cref="StockReservationExpired" path="/remarks/node()"/>
/// <para>
/// <b>The one wait whose far end is this same service.</b> Every other expiry
/// here bounds a peer — Inventory, Payments, Shipping — where this one bounds
/// Ordering answering its own command, so the thing it is really waiting out
/// is §9.8's retry budget on <c>ordering-commands</c> rather than a network of
/// services. That is what sets its delay, and
/// <see cref="OrderFulfilmentSaga"/> argues the number at the schedule.
/// </para>
/// </remarks>
public sealed record ConfirmationExpired(Guid OrderId);

/// <inheritdoc cref="StockReservationExpired"/>
public sealed record DespatchExpired(Guid OrderId);

/// <inheritdoc cref="StockReservationExpired"/>
public sealed record StockReleaseExpired(Guid OrderId);
