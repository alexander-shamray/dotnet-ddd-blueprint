using Common.Contracts;
using Common.Contracts.Catalog.V1;
using Common.Contracts.Inventory.V1;
using Common.Contracts.Ordering.V1;
using Common.Contracts.Payments.V1;
using Common.Contracts.Shipping.V1;

namespace Platform.IntegrationTests;

/// <summary>
/// One populated instance per contract type, hand-written. This is what keeps
/// §12.6's suite honest as contracts grow: every member of a V1 contract is
/// <c>required</c>, so there is no reflection shortcut that constructs one, and
/// a new contract without a sample fails <see cref="ContractTests"/> rather
/// than being quietly skipped — which is the failure mode of every "iterate
/// over all the types" test that falls back to
/// <c>Activator.CreateInstance</c>.
/// </summary>
/// <remarks>
/// Every sample carries a <b>distinct, non-default value in every member</b>,
/// and that is a rule rather than a habit. Two assertions read these, and both
/// go quiet on a sample of zeroes: the round-trip serialises, deserialises and
/// re-serialises, so a member a serialiser dropped entirely is absent from both
/// forms and compares equal; and the wire-member check asks that every declared
/// property appears in the JSON, which a defaulted one still does. A sample
/// that cannot distinguish a working serialiser from a broken one is a sample
/// that turns both assertions into ones that cannot fail.
/// </remarks>
internal static class ContractSamples
{
    private static readonly Guid Message = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Correlation = new("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Order = new("33333333-3333-3333-3333-333333333333");
    private static readonly Guid Customer = new("44444444-4444-4444-4444-444444444444");
    private static readonly Guid Product = new("55555555-5555-5555-5555-555555555555");

    // A fixed instant with a non-zero offset, on purpose: DateTimeOffset.MinValue
    // survives every serialiser bug there is, and a UTC-offset sample cannot
    // catch an options change that normalises the offset away.
    private static readonly DateTimeOffset Occurred =
        new(2026, 8, 11, 9, 30, 0, TimeSpan.FromHours(2));

    private static readonly Dictionary<Type, Func<object>> Registry = new()
    {
        [typeof(ProductPublished)] = () => new ProductPublished
        {
            MessageId = Message,
            CorrelationId = Correlation,
            OccurredAt = Occurred,
            ProductId = Product,
            Name = "Espresso machine",
            ThumbnailUrl = "https://cdn.example.test/espresso.png",
            Amount = 499.99m,
            Currency = "EUR"
        },
        [typeof(PriceChanged)] = () => new PriceChanged
        {
            MessageId = Message,
            CorrelationId = Correlation,
            OccurredAt = Occurred,
            ProductId = Product,
            Amount = 449.50m,
            Currency = "EUR"
        },
        [typeof(ProductDiscontinued)] = () => new ProductDiscontinued
        {
            MessageId = Message,
            CorrelationId = Correlation,
            OccurredAt = Occurred,
            ProductId = Product
        },
        [typeof(OrderPlaced)] = () => new OrderPlaced
        {
            MessageId = Message,
            CorrelationId = Correlation,
            OccurredAt = Occurred,
            OrderId = Order,
            CustomerId = Customer,
            TotalAmount = 999.98m,
            Currency = "EUR",
            Lines = [new PlacedLine(Product, 2, 499.99m)]
        },
        [typeof(PlacedLine)] = () => new PlacedLine(Product, 2, 499.99m),
        [typeof(OrderConfirmed)] = () => new OrderConfirmed
        {
            MessageId = Message,
            CorrelationId = Correlation,
            OccurredAt = Occurred,
            OrderId = Order,
            CustomerId = Customer,
            TotalAmount = 999.98m,
            Currency = "EUR",
            Lines = [new ConfirmedLine(Product, 2, 499.99m)],
            ShippingAddress = new ShippingAddressV1("12 Rue de la Paix", "Paris", "75002", "FR")
        },
        [typeof(ConfirmedLine)] = () => new ConfirmedLine(Product, 2, 499.99m),
        [typeof(ShippingAddressV1)] = () =>
            new ShippingAddressV1("12 Rue de la Paix", "Paris", "75002", "FR"),
        [typeof(OrderCancelled)] = () => new OrderCancelled
        {
            MessageId = Message,
            CorrelationId = Correlation,
            OccurredAt = Occurred,
            OrderId = Order,
            CustomerId = Customer,
            Reason = CancelReasons.PaymentTimeout
        },
        [typeof(CancelOrder)] = () => new CancelOrder(Order, CancelReasons.OutOfStock),
        [typeof(ConfirmOrder)] = () => new ConfirmOrder(Order, "psp_ref_9f21"),
        [typeof(MarkOrderShipped)] = () => new MarkOrderShipped(Order, "TRK-99182"),
        [typeof(FlagOrderForReview)] = () =>
            new FlagOrderForReview(Order, ReviewReasons.NotDespatched),
        [typeof(StockReserved)] = () => new StockReserved
        {
            MessageId = Message,
            CorrelationId = Correlation,
            OccurredAt = Occurred,
            OrderId = Order
        },
        [typeof(StockReservationFailed)] = () => new StockReservationFailed
        {
            MessageId = Message,
            CorrelationId = Correlation,
            OccurredAt = Occurred,
            OrderId = Order,
            UnavailableProductIds = [Product]
        },
        [typeof(StockReleased)] = () => new StockReleased
        {
            MessageId = Message,
            CorrelationId = Correlation,
            OccurredAt = Occurred,
            OrderId = Order
        },
        [typeof(StockLevelChanged)] = () => new StockLevelChanged
        {
            MessageId = Message,
            CorrelationId = Correlation,
            OccurredAt = Occurred,
            ProductId = Product,
            QuantityAvailable = 17
        },
        [typeof(ReserveStock)] = () => new ReserveStock(Order, [new StockLine(Product, 2)]),
        [typeof(ReleaseStock)] = () => new ReleaseStock(Order),
        [typeof(StockLine)] = () => new StockLine(Product, 2),
        [typeof(PaymentAuthorised)] = () => new PaymentAuthorised
        {
            MessageId = Message,
            CorrelationId = Correlation,
            OccurredAt = Occurred,
            OrderId = Order,
            Reference = "psp_ref_9f21",
            Amount = 999.98m,
            Currency = "EUR"
        },
        [typeof(PaymentDeclined)] = () => new PaymentDeclined
        {
            MessageId = Message,
            CorrelationId = Correlation,
            OccurredAt = Occurred,
            OrderId = Order,
            Reason = "insufficient_funds"
        },
        [typeof(PaymentRefunded)] = () => new PaymentRefunded
        {
            MessageId = Message,
            CorrelationId = Correlation,
            OccurredAt = Occurred,
            OrderId = Order,
            Reference = "psp_ref_9f21",
            Amount = 999.98m,
            Currency = "EUR"
        },
        [typeof(AuthorisePayment)] = () => new AuthorisePayment(Order, Customer, 999.98m, "EUR"),
        [typeof(ShipmentDispatched)] = () => new ShipmentDispatched
        {
            MessageId = Message,
            CorrelationId = Correlation,
            OccurredAt = Occurred,
            OrderId = Order,
            TrackingNumber = "TRK-99182"
        },
        [typeof(ShipmentDelivered)] = () => new ShipmentDelivered
        {
            MessageId = Message,
            CorrelationId = Correlation,
            OccurredAt = Occurred,
            OrderId = Order,
            TrackingNumber = "TRK-99182"
        }
    };

    /// <summary>
    /// The sample for one contract type, or a failure naming the type and what
    /// to do about it. Throwing is the whole design: a caller that returned
    /// null would let the suite skip a contract nobody wrote a sample for.
    /// </summary>
    public static object Create(Type contract) =>
        Registry.TryGetValue(contract, out Func<object>? sample) ? sample()
            : throw new InvalidOperationException(
                $"No sample for the contract '{contract.FullName}'. Every member of a V1 " +
                "contract is required (§12.6), so nothing can construct one by reflection — " +
                "add an entry to ContractSamples.");

    /// <summary>
    /// The types a sample exists for, so a test can assert the registry holds
    /// no entry for a type that has since been deleted — the other direction of
    /// the same drift, and the one throwing cannot catch.
    /// </summary>
    public static IReadOnlyCollection<Type> Sampled => Registry.Keys;
}
