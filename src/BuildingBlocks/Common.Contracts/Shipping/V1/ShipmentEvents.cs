namespace Common.Contracts.Shipping.V1;

/// <summary>
/// A shipment left the warehouse (§3.2). The saga marks the order shipped on it
/// and finalises (§9.6); Inventory and Notifications consume it too.
/// </summary>
/// <remarks>
/// <see cref="TrackingNumber"/> is a string here and a value object in
/// Ordering's domain, which is the ordinary shape of every identifier crossing
/// this boundary (§9.1).
/// </remarks>
public sealed record ShipmentDispatched : IIntegrationEvent
{
    public required Guid MessageId { get; init; }

    public required Guid CorrelationId { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public required Guid OrderId { get; init; }

    public required string TrackingNumber { get; init; }
}

/// <summary>
/// A shipment reached the customer (§3.2). Notifications is its only consumer —
/// the saga has already finalised on despatch, because delivery is not
/// something it can coordinate or compensate.
/// </summary>
public sealed record ShipmentDelivered : IIntegrationEvent
{
    public required Guid MessageId { get; init; }

    public required Guid CorrelationId { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public required Guid OrderId { get; init; }

    public required string TrackingNumber { get; init; }
}
