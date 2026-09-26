using Storage.Domain.Catalog;
using Storage.Domain.Common;

namespace Storage.Domain.Tests.Catalog;

public class CategoryTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();

    [Fact]
    public void A_root_starts_at_depth_zero_with_no_parent()
    {
        var beverages = Category.CreateRoot(Tenant, "Bebidas");

        Assert.Null(beverages.ParentId);
        Assert.Equal(0, beverages.Depth);
        Assert.True(beverages.IsRoot);
        Assert.Equal($"/{beverages.Id:N}/", beverages.Path);
    }

    [Fact]
    public void A_child_carries_its_parent_path()
    {
        var beverages = Category.CreateRoot(Tenant, "Bebidas");

        var energy = beverages.CreateChild("Energéticos");

        Assert.Equal(beverages.Id, energy.ParentId);
        Assert.Equal(1, energy.Depth);
        Assert.StartsWith(beverages.Path, energy.Path, StringComparison.Ordinal);
    }

    [Fact]
    public void Descendants_are_recognised_down_the_whole_branch()
    {
        var beverages = Category.CreateRoot(Tenant, "Bebidas");
        var energy = beverages.CreateChild("Energéticos");
        var sugarFree = energy.CreateChild("Zero açúcar");

        Assert.True(energy.IsDescendantOf(beverages));
        Assert.True(sugarFree.IsDescendantOf(beverages));
        Assert.False(beverages.IsDescendantOf(energy));
    }

    [Fact]
    public void Siblings_are_not_descendants_of_each_other()
    {
        var beverages = Category.CreateRoot(Tenant, "Bebidas");
        var energy = beverages.CreateChild("Energéticos");
        var sodas = beverages.CreateChild("Refrigerantes");

        // This is the whole point of the tree: a rule aimed at energy drinks must not
        // reach the sodas sitting next to them.
        Assert.False(sodas.IsDescendantOf(energy));
        Assert.False(energy.IsDescendantOf(sodas));
    }

    [Fact]
    public void Ancestors_are_read_off_the_path_without_a_query()
    {
        var beverages = Category.CreateRoot(Tenant, "Bebidas");
        var energy = beverages.CreateChild("Energéticos");
        var sugarFree = energy.CreateChild("Zero açúcar");

        Assert.Equal([beverages.Id, energy.Id], sugarFree.AncestorIds);
        Assert.Equal([beverages.Id, energy.Id, sugarFree.Id], sugarFree.SelfAndAncestorIds);
    }

    [Fact]
    public void Renaming_never_touches_the_path()
    {
        var beverages = Category.CreateRoot(Tenant, "Bebidas");
        var pathBefore = beverages.Path;

        beverages.Rename("Bebidas geladas");

        Assert.Equal(pathBefore, beverages.Path);
    }

    [Fact]
    public void Moving_a_branch_rewrites_every_descendant()
    {
        var beverages = Category.CreateRoot(Tenant, "Bebidas");
        var grocery = Category.CreateRoot(Tenant, "Mercearia");
        var energy = beverages.CreateChild("Energéticos");
        var sugarFree = energy.CreateChild("Zero açúcar");

        energy.MoveTo(grocery, [sugarFree]);

        Assert.Equal(grocery.Id, energy.ParentId);
        Assert.StartsWith(grocery.Path, energy.Path, StringComparison.Ordinal);
        Assert.StartsWith(energy.Path, sugarFree.Path, StringComparison.Ordinal);
        Assert.Equal(1, energy.Depth);
        Assert.Equal(2, sugarFree.Depth);
        Assert.True(sugarFree.IsDescendantOf(grocery));
        Assert.False(sugarFree.IsDescendantOf(beverages));
    }

    [Fact]
    public void Moving_a_branch_to_the_root_shifts_the_depths_back()
    {
        var beverages = Category.CreateRoot(Tenant, "Bebidas");
        var energy = beverages.CreateChild("Energéticos");
        var sugarFree = energy.CreateChild("Zero açúcar");

        energy.MoveTo(null, [sugarFree]);

        Assert.True(energy.IsRoot);
        Assert.Equal(0, energy.Depth);
        Assert.Equal(1, sugarFree.Depth);
        Assert.Equal($"/{energy.Id:N}/", energy.Path);
    }

    [Fact]
    public void A_category_cannot_be_moved_under_its_own_descendant()
    {
        var beverages = Category.CreateRoot(Tenant, "Bebidas");
        var energy = beverages.CreateChild("Energéticos");

        // Allowing this would cut the whole branch loose from every root.
        DomainAssert.Breaks(DomainErrors.CategoryMoveIntoOwnBranch, () => beverages.MoveTo(energy, [energy]));
    }

    [Fact]
    public void A_category_cannot_be_moved_under_itself()
    {
        var beverages = Category.CreateRoot(Tenant, "Bebidas");

        DomainAssert.Breaks(DomainErrors.CategoryMoveIntoOwnBranch, () => beverages.MoveTo(beverages, []));
    }

    [Fact]
    public void Moving_refuses_a_node_that_is_not_a_descendant()
    {
        var beverages = Category.CreateRoot(Tenant, "Bebidas");
        var grocery = Category.CreateRoot(Tenant, "Mercearia");
        var energy = beverages.CreateChild("Energéticos");
        var cleaning = grocery.CreateChild("Limpeza");

        // Passing the wrong set would silently corrupt the other branch's paths.
        Assert.Throws<InvalidOperationException>(() => energy.MoveTo(grocery, [cleaning]));
    }

    [Fact]
    public void A_refused_move_leaves_the_whole_branch_untouched()
    {
        var beverages = Category.CreateRoot(Tenant, "Bebidas");
        var grocery = Category.CreateRoot(Tenant, "Mercearia");
        var energy = beverages.CreateChild("Energéticos");
        var sugarFree = energy.CreateChild("Zero açúcar");
        var cleaning = grocery.CreateChild("Limpeza");
        var energyPath = energy.Path;
        var sugarFreePath = sugarFree.Path;

        // The valid descendant comes first: a move that validated while rewriting would
        // already have changed it by the time the foreign node is reached.
        Assert.Throws<InvalidOperationException>(() => energy.MoveTo(grocery, [sugarFree, cleaning]));

        Assert.Equal(beverages.Id, energy.ParentId);
        Assert.Equal(energyPath, energy.Path);
        Assert.Equal(sugarFreePath, sugarFree.Path);
        Assert.Equal(2, sugarFree.Depth);
    }

    [Fact]
    public void A_name_over_the_limit_is_refused()
    {
        DomainAssert.Breaks(
            DomainErrors.CategoryNameInvalid,
            () => Category.CreateRoot(Tenant, new string('x', Category.NameMaxLength + 1)));
    }

    [Fact]
    public void A_branch_cannot_be_grafted_onto_another_shops_tree()
    {
        var mine = Category.CreateRoot(Tenant, "Bebidas");
        var theirs = Category.CreateRoot(Guid.CreateVersion7(), "Mercearia");

        DomainAssert.Breaks(DomainErrors.CategoryMoveAcrossShops, () => mine.MoveTo(theirs, []));
    }

    [Fact]
    public void A_child_always_belongs_to_its_parents_shop()
    {
        var beverages = Category.CreateRoot(Tenant, "Bebidas");

        Assert.Equal(Tenant, beverages.CreateChild("Energéticos").TenantId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void A_category_needs_a_name(string? name)
    {
        DomainAssert.Breaks(DomainErrors.CategoryNameInvalid, () => Category.CreateRoot(Tenant, name!));
    }

    [Fact]
    public void Names_are_trimmed()
    {
        Assert.Equal("Bebidas", Category.CreateRoot(Tenant, "  Bebidas  ").Name);
    }

    [Fact]
    public void Deactivating_keeps_the_node_in_the_tree()
    {
        var beverages = Category.CreateRoot(Tenant, "Bebidas");
        var energy = beverages.CreateChild("Energéticos");

        beverages.Deactivate();

        Assert.False(beverages.Active);
        Assert.True(energy.IsDescendantOf(beverages));
    }
}
