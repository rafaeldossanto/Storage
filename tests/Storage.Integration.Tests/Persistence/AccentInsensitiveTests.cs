using System.Text.RegularExpressions;
using Storage.Infrastructure.Persistence;

namespace Storage.Integration.Tests.Persistence;

public sealed class AccentInsensitiveTests
{
    [Theory]
    [InlineData("Açúcar", "acucar")]
    [InlineData("SABÃO EM PÓ", "sabao em po")]
    [InlineData("Pão de Queijo", "pao de queijo")]
    [InlineData("Jalapeño", "jalapeno")]
    [InlineData("Leite (1L)", "leite (1l)")]
    public void Folding_drops_accents_and_case(string text, string folded)
    {
        Assert.Equal(folded, AccentInsensitive.Fold(text));
    }

    [Theory]
    [InlineData("acucar", "Açúcar Cristal")]
    [InlineData("coracao", "Coração de Frango")]
    [InlineData("pao", "Pão Francês")]
    [InlineData("pão", "Pao Frances")]
    public void The_pattern_matches_with_or_without_accents(string typed, string name)
    {
        Assert.Matches(new Regex(AccentInsensitive.Pattern(typed), RegexOptions.IgnoreCase), name);
    }

    [Theory]
    [InlineData("(1L")]
    [InlineData("a+b")]
    [InlineData(".*")]
    [InlineData("[x]")]
    public void Regex_syntax_typed_in_a_search_is_text(string typed)
    {
        // Were any of these read as syntax, the pattern would fail to compile or match
        // everything; as text it only matches itself.
        var pattern = new Regex(AccentInsensitive.Pattern(typed), RegexOptions.IgnoreCase);

        Assert.Matches(pattern, $"produto {typed} teste");
        Assert.DoesNotMatch(pattern, "outro produto");
    }
}
