using System.Text.Json;
using Storage.Domain.Catalog;

namespace Storage.Application.Accounts;

/// <summary>
/// The category tree a new shop starts with, so the first screen is not an empty page.
/// </summary>
/// <remarks>
/// The names are content the shopkeeper will rename and rearrange, not text of the
/// application - but they are still Portuguese, so they live in a data file embedded in
/// the assembly rather than as literals in the code. Another language is another file.
/// </remarks>
internal static class StarterCatalog
{
    private const string ResourceName = "default-categories.pt-BR.json";

    private static readonly Lazy<IReadOnlyList<Node>> Tree = new(Load);

    public static IReadOnlyList<Category> For(Guid tenantId)
    {
        var categories = new List<Category>();

        foreach (var node in Tree.Value)
        {
            var root = Category.CreateRoot(tenantId, node.Name);
            categories.Add(root);
            categories.AddRange(node.Children.Select(root.CreateChild));
        }

        return categories;
    }

    private static IReadOnlyList<Node> Load()
    {
        using var stream = typeof(StarterCatalog).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{ResourceName}' is missing.");

        return JsonSerializer.Deserialize<Node[]>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException($"Embedded resource '{ResourceName}' is empty.");
    }

    private sealed record Node(string Name, string[] Children);
}
