namespace Ordering.Infrastructure.Messaging;

/// <summary>
/// The four scheduled messages §9.6's saga arms, one per wait state.
/// </summary>
/// <remarks>
/// <b>Not contracts, and deliberately not in <c>Common.Contracts</c>.</b> §3.2
/// lists what each service publishes, consumes and accepts, and none of these
/// appears in any column: they are sent by this saga to itself, they carry no
/// envelope, and no peer may bind them. §4.3 permits exactly one assembly to
/// cross a service boundary and a private timeout is not a candidate for it —
/// putting these there would advertise four messages another service could
/// legitimately publish, which is precisely how a saga acquires a way to be
/// driven from outside.
/// <para>
/// One record per wait rather than one <c>FulfilmentExpired(Guid OrderId,
/// string Wait)</c>, because MassTransit correlates a schedule by message
/// <em>type</em>: a single type would make all four schedules the same
/// subscription, and <c>Unschedule</c> would have no way to tell which token
/// it was cancelling.
/// </para>
/// </remarks>
public sealed record StockReservationExpired(Guid OrderId);

/// <inheritdoc cref="StockReservationExpired"/>
public sealed record PaymentAuthorisationExpired(Guid OrderId);

/// <inheritdoc cref="StockReservationExpired"/>
public sealed record DespatchExpired(Guid OrderId);

/// <inheritdoc cref="StockReservationExpired"/>
public sealed record StockReleaseExpired(Guid OrderId);
