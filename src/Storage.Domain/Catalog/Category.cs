using Storage.Domain.Common;

namespace Storage.Domain.Catalog;

/// <summary>
/// A node in the product category tree, for example Beverages > Energy drinks.
/// </summary>
/// <remarks>
/// The tree is stored as a materialised path: every row carries the ids of all its
/// ancestors, so "everything under Beverages" is one indexed LIKE instead of a recursive
/// query, and a product's ancestors can be read straight off its own row without touching
/// the database. That is what makes a discount aimed at one node reach its children
/// without reaching its siblings.
/// </remarks>
public sealed class Category : ITenantScoped
{
    public const int NameMaxLength = 60;
    private const string Separator = "/";

    private Category()
    {
        // EF Core materialisation.
    }

    private Category(Guid id, Guid tenantId, string name, Category? parent)
    {
        Id = id;
        TenantId = tenantId;
        Name = name;
        ParentId = parent?.Id;
        Path = (parent?.Path ?? Separator) + Key(id) + Separator;
        Depth = parent is null ? 0 : parent.Depth + 1;
        Active = true;
    }

    public Guid Id { get; private set; }

    /// <summary>The shop this category belongs to.</summary>
    public Guid TenantId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public Guid? ParentId { get; private set; }

    /// <summary>
    /// Ids of this node and all its ancestors, delimited by slashes, e.g.
    /// <c>/0192.../0193.../</c>. Ids rather than names, so renaming a category never
    /// rewrites a single row.
    /// </summary>
    public string Path { get; private set; } = string.Empty;

    /// <summary>Zero for a root. Used to rank how specific a discount rule is.</summary>
    public int Depth { get; private set; }

    public bool Active { get; private set; }

    public bool IsRoot => ParentId is null;

    public static Category CreateRoot(Guid tenantId, string name) =>
        new(Guid.CreateVersion7(), tenantId, Validate(name), parent: null);

    /// <summary>A child always belongs to the same shop as its parent.</summary>
    public Category CreateChild(string name) =>
        new(Guid.CreateVersion7(), TenantId, Validate(name), parent: this);

    /// <summary>
    /// The prefix that matches every descendant of this node, for
    /// <c>WHERE Path LIKE @prefix || '%'</c>.
    /// </summary>
    public string DescendantPathPrefix => Path;

    /// <summary>
    /// Ancestor ids read off the path, nearest parent last, without a query.
    /// </summary>
    public IReadOnlyList<Guid> AncestorIds =>
        Path.Split(Separator, StringSplitOptions.RemoveEmptyEntries)
            .SkipLast(1)
            .Select(segment => Guid.ParseExact(segment, "N"))
            .ToArray();

    /// <summary>This node and its ancestors, which is the set a discount rule may target.</summary>
    public IReadOnlyList<Guid> SelfAndAncestorIds =>
        Path.Split(Separator, StringSplitOptions.RemoveEmptyEntries)
            .Select(segment => Guid.ParseExact(segment, "N"))
            .ToArray();

    public bool IsDescendantOf(Category other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Id != other.Id && Path.StartsWith(other.Path, StringComparison.Ordinal);
    }

    public void Rename(string name) => Name = Validate(name);

    public void Activate() => Active = true;

    /// <summary>
    /// Deactivating hides the category from pickers without deleting it, because products
    /// and past sales still point at it.
    /// </summary>
    public void Deactivate() => Active = false;

    /// <summary>
    /// Moves this node under <paramref name="newParent"/> (or to the root when null) and
    /// rewrites the path of everything below it.
    /// </summary>
    /// <param name="descendants">
    /// Every node whose path starts with this node's current path. The caller loads them;
    /// the entity refuses to move if the set is incomplete in the only way it can check.
    /// </param>
    public void MoveTo(Category? newParent, IReadOnlyCollection<Category> descendants)
    {
        ArgumentNullException.ThrowIfNull(descendants);

        // A node cannot become its own descendant's child: that would detach the whole
        // subtree from the root and no query would ever find it again.
        if (newParent is not null && (newParent.Id == Id || newParent.IsDescendantOf(this)))
        {
            throw new InvalidOperationException(
                $"Category '{Name}' cannot be moved under itself or one of its descendants.");
        }

        // One shop's tree can never graft onto another's.
        if (newParent is not null && newParent.TenantId != TenantId)
        {
            throw new InvalidOperationException(
                $"Category '{Name}' cannot be moved into another tenant's tree.");
        }

        if (newParent?.Id == ParentId)
        {
            return;
        }

        var oldPath = Path;
        var oldDepth = Depth;

        ParentId = newParent?.Id;
        Path = (newParent?.Path ?? Separator) + Key(Id) + Separator;
        Depth = newParent is null ? 0 : newParent.Depth + 1;

        var depthShift = Depth - oldDepth;

        foreach (var descendant in descendants)
        {
            if (!descendant.Path.StartsWith(oldPath, StringComparison.Ordinal) || descendant.Id == Id)
            {
                throw new InvalidOperationException(
                    $"Category '{descendant.Name}' is not a descendant of '{Name}'.");
            }

            descendant.Path = Path + descendant.Path[oldPath.Length..];
            descendant.Depth += depthShift;
        }
    }

    private static string Key(Guid id) => id.ToString("N");

    private static string Validate(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var trimmed = name.Trim();

        return trimmed.Length <= NameMaxLength
            ? trimmed
            : throw new ArgumentException(
                $"A category name is limited to {NameMaxLength} characters.", nameof(name));
    }
}
