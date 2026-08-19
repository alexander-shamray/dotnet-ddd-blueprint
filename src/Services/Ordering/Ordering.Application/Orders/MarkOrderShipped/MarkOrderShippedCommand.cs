using Common.Application;
using Ordering.Domain.Orders;

namespace Ordering.Application.Orders.MarkOrderShipped;

/// <summary>
/// Record that an order has been despatched (§9.6). Despatch is Shipping's
/// fact; recording it on the order is Ordering's decision, which is why the
/// saga sends this rather than Ordering subscribing to
/// <c>ShipmentDispatched</c> directly.
/// </summary>
/// <remarks>
/// <see cref="Tracking"/> is the domain type, parsed in the mapper, on
/// <c>ConfirmOrderCommand</c>'s terms.
/// </remarks>
public sealed record MarkOrderShippedCommand(Guid OrderId, TrackingNumber Tracking) : ICommand<Result>;
