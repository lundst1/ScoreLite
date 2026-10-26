using System.Globalization;
using System.Numerics;

namespace ScoreLite.Core;

/// <summary>Tidskonstanter (V2: DIVISION = 480 i MuseScore).</summary>
public static class Time
{
    public const int TicksPerQuarter = 480;
    public const int TicksPerWhole = TicksPerQuarter * 4;
}

/// <summary>
/// Exakt bråk av en helnot. Positioner inom en takt lagras som bråk så att trioler och andra
/// tuplets inte ackumulerar avrundningsfel (AL-01 regel 5). Ticks räknas ut först när en not placeras.
/// </summary>
public readonly record struct Frac(long Num, long Den) : IComparable<Frac>
{
    public static readonly Frac Zero = new(0, 1);

    public static Frac Of(long num, long den)
    {
        if (den == 0) throw new DivideByZeroException();
        if (den < 0) { num = -num; den = -den; }
        long g = (long)BigInteger.GreatestCommonDivisor(Math.Abs(num), den);
        if (g > 1) { num /= g; den /= g; }
        return new Frac(num, den);
    }

    public static Frac operator +(Frac a, Frac b) => Of(a.Num * b.Den + b.Num * a.Den, a.Den * b.Den);
    public static Frac operator -(Frac a, Frac b) => Of(a.Num * b.Den - b.Num * a.Den, a.Den * b.Den);
    public static Frac operator *(Frac a, Frac b) => Of(a.Num * b.Num, a.Den * b.Den);
    public static Frac operator /(Frac a, Frac b) => Of(a.Num * b.Den, a.Den * b.Num);
    public static bool operator <(Frac a, Frac b) => a.CompareTo(b) < 0;
    public static bool operator >(Frac a, Frac b) => a.CompareTo(b) > 0;
    public static bool operator <=(Frac a, Frac b) => a.CompareTo(b) <= 0;
    public static bool operator >=(Frac a, Frac b) => a.CompareTo(b) >= 0;

    public int CompareTo(Frac other) => (Num * other.Den).CompareTo(other.Num * Den);

    /// <summary>Avrundar till närmaste tick (halva uppåt).</summary>
    public int ToTicks()
    {
        // Num/Den helnoter * 1920 ticks
        BigInteger n = (BigInteger)Num * Time.TicksPerWhole;
        BigInteger d = Den;
        BigInteger q = BigInteger.DivRem(n * 2 + d, d * 2, out _);
        // floor((2n + d) / 2d) för icke-negativt n; hantera negativt med golv
        if (n < 0) q = (BigInteger)Math.Floor((double)n / (double)d + 0.5);
        return (int)q;
    }

    public bool IsExactTicks => (Num * Time.TicksPerWhole) % Den == 0;

    public static Frac FromTicks(int ticks) => Of(ticks, Time.TicksPerWhole);

    /// <summary>Tolkar "1/2", "-1/4", "3" eller "4/4".</summary>
    public static bool TryParse(string? s, out Frac value)
    {
        value = Zero;
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim();
        int slash = s.IndexOf('/');
        if (slash < 0)
        {
            if (!long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long n)) return false;
            value = Of(n, 1);
            return true;
        }
        if (!long.TryParse(s.AsSpan(0, slash), NumberStyles.Integer, CultureInfo.InvariantCulture, out long a)) return false;
        if (!long.TryParse(s.AsSpan(slash + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out long b) || b == 0) return false;
        value = Of(a, b);
        return true;
    }

    public override string ToString() => $"{Num}/{Den}";
}
