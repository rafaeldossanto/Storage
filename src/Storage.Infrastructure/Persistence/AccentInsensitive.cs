using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Storage.Infrastructure.Persistence;

/// <summary>
/// Turns what a person typed into a pattern that ignores accents on both sides: "acucar",
/// "açúcar" and "AÇUCAR" all find "Açúcar", and "sabão" finds a product saved as "Sabao".
/// </summary>
/// <remarks>
/// Nobody types accents at the till, and half the catalogue was registered without them. A
/// normalised copy of every name would work too, but would need migrating and keeping in
/// step with the name; folding the pattern instead leaves the stored data as it is. The
/// regex runs with the case-insensitive flag, so only accents need expanding here.
/// </remarks>
internal static class AccentInsensitive
{
    // Every accented form a Brazilian product name uses, by the letter it rests on.
    private static readonly Dictionary<char, string> Variants = new()
    {
        ['a'] = "[aáàâãä]",
        ['e'] = "[eéèêë]",
        ['i'] = "[iíìîï]",
        ['o'] = "[oóòôõö]",
        ['u'] = "[uúùûü]",
        ['c'] = "[cç]",
        ['n'] = "[nñ]",
    };

    /// <summary>A "contains" pattern for the term, safe to hand to the database.</summary>
    public static string Pattern(string term)
    {
        ArgumentNullException.ThrowIfNull(term);

        var pattern = new StringBuilder();

        foreach (var letter in Fold(term.Trim()))
        {
            pattern.Append(Variants.TryGetValue(letter, out var variants)
                ? variants
                // Anything else is taken literally: a "(" or a "." typed in a search is text,
                // never regex syntax.
                : Regex.Escape(letter.ToString()));
        }

        return pattern.ToString();
    }

    /// <summary>Lower case, without accents: "Açúcar" becomes "acucar".</summary>
    public static string Fold(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        // Decomposed, "ú" is "u" followed by a combining acute accent; dropping the combining
        // marks leaves the base letters.
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var folded = new StringBuilder(decomposed.Length);

        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                folded.Append(char.ToLowerInvariant(character));
            }
        }

        return folded.ToString().Normalize(NormalizationForm.FormC);
    }
}
