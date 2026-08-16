using Common.Domain;

namespace Ordering.Domain.Common;

/// <summary>
/// §5.3's always-valid value object: the constructor is private and
/// <see cref="Of"/> is the only way in, so an invalid <see cref="Money"/>
/// cannot be constructed and nothing downstream checks for one.
/// </summary>
/// <remarks>
/// Ordering's own, not a reference to Catalog's. A value object is part of a
/// bounded context's model (§3), so the duplication is the boundary rather
/// than an omission — the two are free to diverge, and a shared one would be
/// the cross-service assembly §4.3 permits exactly one of.
/// </remarks>
public readonly record struct Money
{
    public decimal Amount { get; }
    public string Currency { get; }

    private Money(decimal amount, string currency)
    {
        Amount = amount;
        Currency = currency;
    }

    public static Money Of(decimal amount, string currency)
    {
        if (amount < 0)
            throw new DomainException("Money cannot be negative.");

        // Letters as well as length: "1$?" is three characters and no
        // currency, and a guard that admits it makes the exception message a
        // stricter claim than the type keeps.
        if (currency is not { Length: 3 } || !currency.All(char.IsAsciiLetter))
            throw new DomainException("Currency must be a 3-letter currency code.");

        return new Money(decimal.Round(amount, 2, MidpointRounding.ToEven), currency.ToUpperInvariant());
    }

    public static Money Zero(string currency) => Of(0m, currency);

    public static Money operator +(Money left, Money right)
    {
        EnsureSameCurrency(left, right);
        return new Money(left.Amount + right.Amount, left.Currency);
    }

    public static Money operator *(Money money, int quantity)
    {
        // Without this guard the operator is a back door past Of: a negative
        // quantity would construct the negative Money the factory refuses.
        if (quantity < 0)
            throw new DomainException("Money cannot be multiplied by a negative quantity.");

        return new Money(money.Amount * quantity, money.Currency);
    }

    private static void EnsureSameCurrency(Money left, Money right)
    {
        if (left.Currency != right.Currency)
            throw new DomainException(
                $"Cannot combine {left.Currency} with {right.Currency}.");
    }
}
