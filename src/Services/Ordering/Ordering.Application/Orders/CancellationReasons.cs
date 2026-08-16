using System.Collections.Frozen;
using Ordering.Domain.Orders;

namespace Ordering.Application.Orders;

/// <summary>
/// The wire vocabulary for <see cref="CancellationReason"/> (§11.4). Every
/// code a caller or a sibling service may send, and the enum member each one
/// means.
/// </summary>
/// <remarks>
/// <b>It refuses a code it does not know rather than defaulting.</b> A sibling
/// service sending a code we do not recognise is a deployment problem, and no
/// amount of backoff resolves it — a default would turn it into an order
/// cancelled for the wrong recorded reason, which is worse than a rejection
/// because nothing later can tell the two apart. §13.3 tags a metric with the
/// reason, so the set has to be closed for that as well.
/// </remarks>
public static class CancellationReasons
{
    private static readonly FrozenDictionary<string, CancellationReason> ByCode =
        new Dictionary<string, CancellationReason>(StringComparer.Ordinal)
        {
            [CancelReasons.OutOfStock] = CancellationReason.OutOfStock,
            [CancelReasons.StockTimeout] = CancellationReason.StockTimeout,
            [CancelReasons.PaymentDeclined] = CancellationReason.PaymentDeclined,
            [CancelReasons.PaymentTimeout] = CancellationReason.PaymentTimeout,
            [CancelReasons.CustomerRequest] = CancellationReason.CustomerRequest
        }.ToFrozenDictionary();

    public static bool TryParse(string? code, out CancellationReason reason) =>
        ByCode.TryGetValue(code ?? "", out reason);

    // The reverse, for anything that has the enum and needs the vocabulary
    // back — §13.3's metric tag, and the saga when it re-publishes. Built by
    // inverting the map above rather than written twice: a second table is a
    // second thing to forget when a reason is added.
    private static readonly FrozenDictionary<CancellationReason, string> ToCodeMap =
        ByCode.ToFrozenDictionary(p => p.Value, p => p.Key);

    public static string ToCode(CancellationReason reason) => ToCodeMap[reason];
}

/// <summary>
/// The codes themselves. Constants rather than literals, because the endpoint
/// parses them and §9.6's saga will send them — two places that must agree,
/// and a misspelling in either is a rejected cancellation at runtime.
/// </summary>
public static class CancelReasons
{
    public const string OutOfStock = "out_of_stock";
    public const string StockTimeout = "stock_timeout";
    public const string PaymentDeclined = "payment_declined";
    public const string PaymentTimeout = "payment_timeout";
    public const string CustomerRequest = "customer_request";
}
