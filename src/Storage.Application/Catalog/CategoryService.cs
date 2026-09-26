using Storage.Application.Abstractions;
using Storage.Application.Errors;
using Storage.Domain.Catalog;

namespace Storage.Application.Catalog;

public sealed class CategoryService(ICategoryRepository categories, ITenantContext tenant)
{
    /// <summary>
    /// The shop's categories as a nested tree, siblings in alphabetical order.
    /// </summary>
    public async Task<IReadOnlyList<CategoryTreeNode>> GetTreeAsync(
        CancellationToken cancellationToken = default)
    {
        var all = await categories.ListAsync(cancellationToken);

        var childrenOf = all.ToLookup(category => category.ParentId);

        // Invariant culture rather than pt-BR: it still sorts "Águas" next to "Açougue"
        // instead of after "Zero", without the backend knowing which language the shop reads.
        var byName = StringComparer.InvariantCultureIgnoreCase;

        IReadOnlyList<CategoryTreeNode> Build(Guid? parentId) =>
            childrenOf[parentId]
                .OrderBy(category => category.Name, byName)
                .Select(category => new CategoryTreeNode(
                    category.Id,
                    category.Name,
                    category.Depth,
                    category.Active,
                    Build(category.Id)))
                .ToArray();

        return Build(parentId: null);
    }

    public async Task<CategoryDto> CreateAsync(
        CreateCategoryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        Category category;

        if (request.ParentId is { } parentId)
        {
            var parent = await categories.FindAsync(parentId, cancellationToken)
                ?? throw UseCaseException.NotFound(
                    ErrorCodes.ParentCategoryNotFound,
                    $"Parent category {parentId} does not exist.");

            category = parent.CreateChild(request.Name);
        }
        else
        {
            category = Category.CreateRoot(tenant.TenantId, request.Name);
        }

        await categories.AddAsync(category, cancellationToken);
        return category.ToDto();
    }

    public async Task<CategoryDto> RenameAsync(
        Guid id,
        RenameCategoryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var category = await RequireAsync(id, cancellationToken);
        category.Rename(request.Name);

        await categories.UpdateAsync(category, cancellationToken);
        return category.ToDto();
    }

    /// <summary>
    /// Moves a category and its whole branch under a new parent, or to the root.
    /// </summary>
    public async Task<CategoryDto> MoveAsync(
        Guid id,
        MoveCategoryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var category = await RequireAsync(id, cancellationToken);

        Category? newParent = null;
        if (request.ParentId is { } parentId)
        {
            newParent = await categories.FindAsync(parentId, cancellationToken)
                ?? throw UseCaseException.NotFound(
                    ErrorCodes.ParentCategoryNotFound,
                    $"Parent category {parentId} does not exist.");
        }

        // Loaded before the move: the query matches the branch by its current path, which
        // is exactly what the move is about to rewrite.
        var branch = await categories.ListDescendantsAsync(category, cancellationToken);

        category.MoveTo(newParent, branch);

        await categories.UpdateManyAsync([category, .. branch], cancellationToken);
        return category.ToDto();
    }

    public async Task<CategoryDto> ActivateAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var category = await RequireAsync(id, cancellationToken);
        category.Activate();

        await categories.UpdateAsync(category, cancellationToken);
        return category.ToDto();
    }

    /// <summary>
    /// Hides the category from pickers. Its products stay where they are: deactivating is
    /// about what shows up when registering new things, not about what already exists.
    /// </summary>
    public async Task<CategoryDto> DeactivateAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var category = await RequireAsync(id, cancellationToken);
        category.Deactivate();

        await categories.UpdateAsync(category, cancellationToken);
        return category.ToDto();
    }

    private async Task<Category> RequireAsync(Guid id, CancellationToken cancellationToken) =>
        await categories.FindAsync(id, cancellationToken)
        ?? throw UseCaseException.NotFound(ErrorCodes.CategoryNotFound, $"Category {id} does not exist.");
}
