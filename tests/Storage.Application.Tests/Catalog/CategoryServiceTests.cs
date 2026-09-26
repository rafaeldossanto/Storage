using Storage.Application.Catalog;
using Storage.Application.Errors;
using Storage.Application.Tests.Fakes;

namespace Storage.Application.Tests.Catalog;

public sealed class CategoryServiceTests
{
    private static readonly Guid Shop = Guid.CreateVersion7();

    private readonly InMemoryCategoryRepository _categories;
    private readonly CategoryService _service;

    public CategoryServiceTests()
    {
        var tenant = new FixedTenant(Shop);
        _categories = new InMemoryCategoryRepository(tenant);
        _service = new CategoryService(_categories, tenant);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_root_category_belongs_to_the_current_shop()
    {
        await _service.CreateAsync(new CreateCategoryRequest("Bebidas", ParentId: null), Token);

        Assert.Equal(Shop, Assert.Single(_categories.Stored).TenantId);
    }

    [Fact]
    public async Task The_tree_comes_back_nested_with_siblings_in_alphabetical_order()
    {
        var beverages = await _service.CreateAsync(new CreateCategoryRequest("Bebidas", null), Token);
        await _service.CreateAsync(new CreateCategoryRequest("Refrigerantes", beverages.Id), Token);
        await _service.CreateAsync(new CreateCategoryRequest("Águas", beverages.Id), Token);
        await _service.CreateAsync(new CreateCategoryRequest("Energéticos", beverages.Id), Token);
        await _service.CreateAsync(new CreateCategoryRequest("Mercearia", null), Token);

        var tree = await _service.GetTreeAsync(Token);

        Assert.Equal(["Bebidas", "Mercearia"], tree.Select(node => node.Name));

        // "Águas" sorts with the A's, not after Z as a plain ordinal compare would put it.
        Assert.Equal(
            ["Águas", "Energéticos", "Refrigerantes"],
            tree[0].Children.Select(node => node.Name));

        Assert.Empty(tree[1].Children);
    }

    [Fact]
    public async Task Creating_under_a_parent_that_does_not_exist_is_not_found()
    {
        await Refused.WithAsync(
            ErrorKind.NotFound,
            ErrorCodes.ParentCategoryNotFound,
            () => _service.CreateAsync(new CreateCategoryRequest("Energéticos", Guid.CreateVersion7()), Token));
    }

    [Fact]
    public async Task Moving_a_category_persists_its_whole_branch()
    {
        var beverages = await _service.CreateAsync(new CreateCategoryRequest("Bebidas", null), Token);
        var grocery = await _service.CreateAsync(new CreateCategoryRequest("Mercearia", null), Token);
        var energy = await _service.CreateAsync(new CreateCategoryRequest("Energéticos", beverages.Id), Token);
        var sugarFree = await _service.CreateAsync(new CreateCategoryRequest("Zero açúcar", energy.Id), Token);

        await _service.MoveAsync(energy.Id, new MoveCategoryRequest(grocery.Id), Token);

        // Writing only the moved node would strand its children under the old path.
        Assert.Equal(
            [energy.Id, sugarFree.Id],
            _categories.LastBulkUpdate.Order());

        var groceryPath = _categories.Stored.Single(c => c.Id == grocery.Id).Path;
        var sugarFreePath = _categories.Stored.Single(c => c.Id == sugarFree.Id).Path;
        Assert.StartsWith(groceryPath, sugarFreePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Moving_under_a_parent_that_does_not_exist_is_not_found()
    {
        var beverages = await _service.CreateAsync(new CreateCategoryRequest("Bebidas", null), Token);

        await Refused.WithAsync(
            ErrorKind.NotFound,
            ErrorCodes.ParentCategoryNotFound,
            () => _service.MoveAsync(beverages.Id, new MoveCategoryRequest(Guid.CreateVersion7()), Token));
    }

    [Fact]
    public async Task Renaming_a_category_that_does_not_exist_is_not_found()
    {
        await Refused.WithAsync(
            ErrorKind.NotFound,
            ErrorCodes.CategoryNotFound,
            () => _service.RenameAsync(Guid.CreateVersion7(), new RenameCategoryRequest("Bebidas"), Token));
    }

    [Fact]
    public async Task Deactivating_keeps_the_category_in_the_tree()
    {
        var beverages = await _service.CreateAsync(new CreateCategoryRequest("Bebidas", null), Token);

        var deactivated = await _service.DeactivateAsync(beverages.Id, Token);
        var tree = await _service.GetTreeAsync(Token);

        Assert.False(deactivated.Active);
        Assert.False(Assert.Single(tree).Active);
    }
}
