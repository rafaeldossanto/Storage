using Storage.Domain.Catalog;

namespace Storage.Domain.Tests.Catalog;

public class CategoryTests
{
    [Fact]
    public void A_root_starts_at_depth_zero_with_no_parent()
    {
        var beverages = Category.CreateRoot("Bebidas");

        Assert.Null(beverages.ParentId);
        Assert.Equal(0, beverages.Depth);
        Assert.True(beverages.IsRoot);
        Assert.Equal($"/{beverages.Id:N}/", beverages.Path);
    }

    [Fact]
    public void A_child_carries_its_parent_path()
    {
        var beverages = Category.CreateRoot("Bebidas");

        var energy = beverages.CreateChild("Energéticos");

        Assert.Equal(beverages.Id, energy.ParentId);
        Assert.Equal(1, energy.Depth);
        Assert.StartsWith(beverages.Path, energy.Path, StringComparison.Ordinal);
    }

    [Fact]
    public void Descendants_are_recognised_down_the_whole_branch()
    {
        var beverages = Category.CreateRoot("Bebidas");
        var energy = beverages.CreateChild("Energéticos");
        var sugarFree = energy.CreateChild("Zero açúcar");

        Assert.True(energy.IsDescendantOf(beverages));
        Assert.True(sugarFree.IsDescendantOf(beverages));
        Assert.False(beverages.IsDescendantOf(energy));
    }

    [Fact]
    public void Siblings_are_not_descendants_of_each_other()
    {
        var beverages = Category.CreateRoot("Bebidas");
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
        var beverages = Category.CreateRoot("Bebidas");
        var energy = beverages.CreateChild("Energéticos");
        var sugarFree = energy.CreateChild("Zero açúcar");

        Assert.Equal([beverages.Id, energy.Id], sugarFree.AncestorIds);
        Assert.Equal([beverages.Id, energy.Id, sugarFree.Id], sugarFree.SelfAndAncestorIds);
    }

    [Fact]
    public void Renaming_never_touches_the_path()
    {
        var beverages = Category.CreateRoot("Bebidas");
        var pathBefore = beverages.Path;

        beverages.Rename("Bebidas geladas");

        Assert.Equal(pathBefore, beverages.Path);
    }

    [Fact]
    public void Moving_a_branch_rewrites_every_descendant()
    {
        var beverages = Category.CreateRoot("Bebidas");
        var grocery = Category.CreateRoot("Mercearia");
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
        var beverages = Category.CreateRoot("Bebidas");
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
        var beverages = Category.CreateRoot("Bebidas");
        var energy = beverages.CreateChild("Energéticos");

        // Allowing this would cut the whole branch loose from every root.
        Assert.Throws<InvalidOperationException>(() => beverages.MoveTo(energy, [energy]));
    }

    [Fact]
    public void A_category_cannot_be_moved_under_itself()
    {
        var beverages = Category.CreateRoot("Bebidas");

        Assert.Throws<InvalidOperationException>(() => beverages.MoveTo(beverages, []));
    }

    [Fact]
    public void Moving_refuses_a_node_that_is_not_a_descendant()
    {
        var beverages = Category.CreateRoot("Bebidas");
        var grocery = Category.CreateRoot("Mercearia");
        var energy = beverages.CreateChild("Energéticos");
        var cleaning = grocery.CreateChild("Limpeza");

        // Passing the wrong set would silently corrupt the other branch's paths.
        Assert.Throws<InvalidOperationException>(() => energy.MoveTo(grocery, [cleaning]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void A_category_needs_a_name(string? name)
    {
        Assert.ThrowsAny<ArgumentException>(() => Category.CreateRoot(name!));
    }

    [Fact]
    public void Names_are_trimmed()
    {
        Assert.Equal("Bebidas", Category.CreateRoot("  Bebidas  ").Name);
    }

    [Fact]
    public void Deactivating_keeps_the_node_in_the_tree()
    {
        var beverages = Category.CreateRoot("Bebidas");
        var energy = beverages.CreateChild("Energéticos");

        beverages.Deactivate();

        Assert.False(beverages.Active);
        Assert.True(energy.IsDescendantOf(beverages));
    }
}
