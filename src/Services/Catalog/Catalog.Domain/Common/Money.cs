using Common.Domain;

namespace Catalog.Domain.Common;

/// <summary>
/// §5.3's value object, in Catalog's own namespace. Not shared with any other
/// service: §4.1 rejects the shared kernel an assembly of common domain types
/// would become, and §3.1's whole argument is that a term must not share a
/// class across contexts. The always-valid principle applies — the constructor
/// is private and <see cref="Of"/> is the only way in, so no code downstream
/// checks for an invalid instance.
/// </summary>
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
            throw new DomainException("Currency must be a 3-letter ISO code.");

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
        // quantity would construct the negative Money the factory refuses,
        // and the always-valid claim above would be false.
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
