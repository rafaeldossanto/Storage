using System.Diagnostics.CodeAnalysis;

namespace Bipe.Domain.ValueObjects;

/// <summary>
/// A barcode number, normalised to its 14 digit GTIN form.
/// </summary>
/// <remarks>
/// GTIN-8, UPC-12, EAN-13 and DUN-14 are the same number padded to different lengths, so
/// storing everything as 14 digits makes "is this the same product?" a plain string
/// comparison and keeps the check digit maths uniform. Leading zeros do not change the
/// checksum, which is why padding is safe.
/// </remarks>
public readonly record struct Gtin
{
    public const int NormalizedLength = 14;

    private Gtin(string value) => Value = value;

    /// <summary>The 14 digit normalised form. This is what goes in the database.</summary>
    public string Value { get; }

    /// <summary>
    /// True for numbers in the GS1 restricted range, which stores assign themselves:
    /// in-store labels and, in Brazil, the codes scales print for weighed goods with the
    /// weight or price embedded in the digits. They are unique to one shop, never global.
    /// </summary>
    public bool IsInternal
    {
        get
        {
            var ean13 = Value[1..];
            return ean13[0] == '2';
        }
    }

    public static Gtin Parse(string? input) =>
        TryParse(input, out var gtin)
            ? gtin
            : throw new ArgumentException($"'{input}' is not a valid GTIN.", nameof(input));

    public static bool TryParse([NotNullWhen(true)] string? input, out Gtin gtin)
    {
        gtin = default;

        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var digits = Sanitize(input);

        if (digits.Length is not (8 or 12 or 13 or 14) || !IsAllDigits(digits))
        {
            return false;
        }

        var normalized = digits.PadLeft(NormalizedLength, '0');

        if (!HasValidCheckDigit(normalized))
        {
            return false;
        }

        gtin = new Gtin(normalized);
        return true;
    }

    /// <summary>
    /// Computes the check digit for a code that does not carry one yet. Used to mint
    /// internal codes for products that arrive without a printed barcode.
    /// </summary>
    public static int CalculateCheckDigit(string withoutCheckDigit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(withoutCheckDigit);

        var digits = Sanitize(withoutCheckDigit);

        if (!IsAllDigits(digits) || digits.Length >= NormalizedLength)
        {
            throw new ArgumentException("Expected digits only, shorter than 14.", nameof(withoutCheckDigit));
        }

        return CheckDigitFor(digits.PadLeft(NormalizedLength - 1, '0'));
    }

    /// <summary>
    /// The shortest form that still round-trips, which is what gets printed on a shelf
    /// label or shown next to the product name.
    /// </summary>
    public string ToDisplay() => Value.TrimStart('0').Length <= 8
        ? Value[^8..]
        : Value[0] == '0' ? Value[1..] : Value;

    public override string ToString() => Value;

    private static string Sanitize(string input) =>
        string.Concat(input.Where(char.IsAsciiDigit));

    private static bool IsAllDigits(string value) =>
        value.Length > 0 && value.All(char.IsAsciiDigit);

    private static bool HasValidCheckDigit(string normalized)
    {
        var expected = CheckDigitFor(normalized[..^1]);
        return expected == normalized[^1] - '0';
    }

    /// <summary>
    /// GS1 mod 10: weight the digits 3 and 1 alternately from the right, then take what
    /// is missing to reach the next multiple of ten.
    /// </summary>
    private static int CheckDigitFor(string body)
    {
        var sum = 0;
        var weight = 3;

        for (var i = body.Length - 1; i >= 0; i--)
        {
            sum += (body[i] - '0') * weight;
            weight = weight == 3 ? 1 : 3;
        }

        return (10 - sum % 10) % 10;
    }
}
