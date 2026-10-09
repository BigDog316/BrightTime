using System.Globalization;

namespace BrightTime.SharedKernel.Primitives;

/// <summary>
/// A minor-unit amount. Integer cents only — program fees, sliding-scale
/// tiers and stipends never want binary floating point, and the schema stores
/// <c>*_cents int</c> throughout.
/// </summary>
public readonly record struct Money(int Cents, string Currency = "USD") : IComparable<Money>
{
    public static readonly Money Zero = new(0);

    public bool IsZero => Cents == 0;

    public static Money FromCents(int cents, string currency = "USD") => new(cents, currency);

    public static Money operator +(Money left, Money right)
    {
        Require(left, right);
        return new Money(left.Cents + right.Cents, left.Currency);
    }

    public static Money operator -(Money left, Money right)
    {
        Require(left, right);
        return new Money(left.Cents - right.Cents, left.Currency);
    }

    public static Money Add(Money left, Money right) => left + right;

    public static Money Subtract(Money left, Money right) => left - right;

    public int CompareTo(Money other)
    {
        Require(this, other);
        return Cents.CompareTo(other.Cents);
    }

    public static bool operator <(Money left, Money right) => left.CompareTo(right) < 0;

    public static bool operator >(Money left, Money right) => left.CompareTo(right) > 0;

    public static bool operator <=(Money left, Money right) => left.CompareTo(right) <= 0;

    public static bool operator >=(Money left, Money right) => left.CompareTo(right) >= 0;

    private static void Require(Money left, Money right)
    {
        if (!string.Equals(left.Currency, right.Currency, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Cannot combine {left.Currency} with {right.Currency}.");
        }
    }

    public string Format(Locale locale)
    {
        ArgumentNullException.ThrowIfNull(locale);
        return (Cents / 100m).ToString("C", locale.ToCulture());
    }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Cents / 100m:0.00} {Currency}");
}
