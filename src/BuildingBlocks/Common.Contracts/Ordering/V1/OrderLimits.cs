namespace Common.Contracts.Ordering.V1;

/// <summary>
/// The bounds an order's lines satisfy. Two hosts enforce them — Ordering on
/// the command it executes, the BFF on the quote it prices — so the numbers
/// live here rather than once in each.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why one owner.</b> A quote that accepts what the order will refuse hands
/// the customer a price for a basket they cannot buy, and moves the refusal
/// from the cart screen — where the quantity they typed is still in front of
/// them — to the checkout screen, where it is not. Any daylight between the
/// two validators is a defect waiting for someone to find it with a quantity
/// of a thousand, and two literals are how daylight appears.
/// </para>
/// <para>
/// <b>Why <c>Common.Contracts</c>.</b> The bound has to be readable from
/// <c>Ordering.Application</c> and from <c>Web.Bff</c>, and §4.3 permits
/// exactly one assembly to cross a service boundary. A constant in Ordering's
/// application layer would make the BFF reference a service's internals to
/// read an integer; a constant in the BFF would make Ordering depend on a
/// host. This assembly is the one place both may look.
/// </para>
/// <para>
/// It is not a message type, which is what everything else in this tree is.
/// That is deliberate rather than an intrusion: the bound is a fact about the
/// shape of an order that crosses a boundary, which is this assembly's whole
/// subject, and the alternative to putting it here is not putting it anywhere.
/// </para>
/// </remarks>
public static class OrderLimits
{
    /// <summary>
    /// The fewest of a product a line may carry. A line for none of something
    /// is a line the caller meant to remove, and reading it as an order for
    /// zero is a basket the customer did not assemble.
    /// </summary>
    public const int MinQuantity = 1;

    /// <summary>
    /// The most of one product a single line may carry. A business-shaped
    /// bound rather than a storage one: a basket wanting more than this is a
    /// wholesale order, which is a different conversation and a different
    /// price.
    /// </summary>
    public const int MaxQuantity = 999;

    /// <summary>
    /// The most lines one order — or one quote for it — may carry.
    /// </summary>
    /// <remarks>
    /// The ceiling is not cosmetic. <c>ProjectedPriceReader</c> expands the
    /// product ids into one SQL parameter each and adds <c>@Currency</c>
    /// beside them, and SQL Server stops at 2,100 — so before a ceiling
    /// existed, an authenticated caller sending enough lines turned a
    /// well-formed request into a 500 rather than a 400. A hundred is a
    /// business-shaped bound well inside that: an order with more lines than
    /// this is a data import, not a checkout.
    /// <para>
    /// <c>PlaceOrderValidatorTests</c> asserts the relation to SQL Server's
    /// limit rather than the value, so raising this past what the query can
    /// ask for fails there — which is the moment to batch the query instead.
    /// </para>
    /// </remarks>
    public const int MaxLines = 100;
}
