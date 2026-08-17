using System.Collections.Frozen;
using Common.Contracts.Ordering.V1;
using Ordering.Domain.Orders;

namespace Ordering.Application.Orders;

/// <summary>
/// The wire vocabulary for <see cref="CancellationReason"/> (§11.4). Every
/// code a caller or a sibling service may send, and the enum member each one
/// means.
/// </summary>
/// <remarks>
/// <b>The codes come from <see cref="CancelReasons"/> in
/// <c>Common.Contracts.Ordering.V1</c>, not from a copy here.</b> They are a
/// contract vocabulary (§9.6) — §9.6's saga sends them and this endpoint
/// parses them — so the two sides must agree, and a second table is a second
/// thing to forget when a reason is added, which is exactly what the map
/// below is built by inverting to avoid.
/// <para>
/// <b>It refuses a code it does not know rather than defaulting.</b> A sibling
/// service sending a code we do not recognise is a deployment problem, and no
/// amount of backoff resolves it — a default would turn it into an order
/// cancelled for the wrong recorded reason, which is worse than a rejection
/// because nothing later can tell the two apart. §13.3 tags a metric with the
/// reason, so the set has to be closed for that as well.
/// </para>
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
