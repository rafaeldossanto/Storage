using Storage.Domain.Catalog;
using Storage.Domain.ValueObjects;

namespace Storage.Domain.Pricing;

public sealed record AppliedDiscount(Guid RuleId, string RuleName, Money Amount);

/// <summary>The price of a product after every rule that reaches it has had its say.</summary>
public sealed record PriceQuote(Money RegularPrice, Money FinalPrice, IReadOnlyList<AppliedDiscount> Applied)
{
    public Money Discount => RegularPrice - FinalPrice;

    public bool IsDiscounted => FinalPrice < RegularPrice;
}

/// <summary>
/// Decides what a product costs today. Pure: no database, no clock - everything it needs is
/// passed in, which is why every rule of the resolution is covered by a plain unit test.
/// </summary>
/// <remarks>
/// <para>The resolution, in order:</para>
/// <list type="number">
/// <item>Candidates are the active rules running today whose target is the product itself
/// or its category or any ancestor of it - never a sibling - and whose expiry window, if
/// any, reaches the batch being priced.</item>
/// <item>The most specific rule wins: one aimed at the product beats any aimed at a
/// category, and a deeper category beats a shallower one. Among equals, the higher priority
/// wins, then the bigger discount.</item>
/// <item>A stackable winner is followed by every other stackable candidate, each applied to
/// the price the previous one left. A non-stackable winner stands alone.</item>
/// <item>Stacked together, rules never take off more than <see cref="DefaultMaxStackedDiscount"/>
/// of the regular price. A single rule is applied in full: the shop set it on purpose.</item>
/// </list>
/// </remarks>
public static class DiscountEngine
{
    /// <summary>At most half the regular price comes off when rules stack.</summary>
    public const int DefaultMaxStackedDiscount = 5_000;

    /// <param name="category">The product's category, whose path names its ancestors.</param>
    /// <param name="batchExpiry">
    /// Expiry of the units being priced - the batch that leaves next (FEFO). Null for goods
    /// that do not expire.
    /// </param>
    public static PriceQuote Quote(
        Product product,
        Category category,
        IEnumerable<DiscountRule> rules,
        DateOnly shopDate,
        DateOnly? batchExpiry,
        int maxStackedDiscountBasisPoints = DefaultMaxStackedDiscount)
    {
        ArgumentNullException.ThrowIfNull(product);
        ArgumentNullException.ThrowIfNull(category);
        ArgumentNullException.ThrowIfNull(rules);

        if (category.Id != product.CategoryId)
        {
            throw new ArgumentException("The category must be the product's own.", nameof(category));
        }

        var regular = product.SalePrice;

        // Each category on the path to the root, with its depth: the root is 0. A category
        // outside this path - a sibling, a cousin - is not in it, so its rules never apply.
        var depthOf = category.SelfAndAncestorIds
            .Select((id, depth) => (id, depth))
            .ToDictionary(entry => entry.id, entry => entry.depth);

        // Aimed at the product itself ranks above any category.
        int? Specificity(DiscountRule rule) => rule.TargetType switch
        {
            DiscountTarget.Product when rule.TargetId == product.Id => int.MaxValue,
            DiscountTarget.Category when depthOf.TryGetValue(rule.TargetId, out var depth) => depth,
            _ => null,
        };

        var ranked = rules
            .Where(rule => rule.TenantId == product.TenantId)
            .Where(rule => rule.RunsOn(shopDate) && rule.ReachesBatch(batchExpiry, shopDate))
            .Select(rule => (Rule: rule, Specificity: Specificity(rule)))
            .Where(candidate => candidate.Specificity is not null)
            .Where(candidate => candidate.Rule.DiscountOn(regular) > Money.Zero)
            .OrderByDescending(candidate => candidate.Specificity)
            .ThenByDescending(candidate => candidate.Rule.Priority)
            .ThenByDescending(candidate => candidate.Rule.DiscountOn(regular))
            .ThenBy(candidate => candidate.Rule.Id)
            .Select(candidate => candidate.Rule)
            .ToArray();

        if (ranked.Length == 0)
        {
            return new PriceQuote(regular, regular, []);
        }

        var winner = ranked[0];

        var toApply = winner.Stackable
            ? ranked.Where(rule => rule.Stackable).ToArray()
            : [winner];

        var price = regular;
        var applied = new List<AppliedDiscount>();

        foreach (var rule in toApply)
        {
            var amount = rule.DiscountOn(price);
            price -= amount;
            applied.Add(new AppliedDiscount(rule.Id, rule.Name, amount));
        }

        if (applied.Count > 1)
        {
            var floor = regular - regular.Percentage(maxStackedDiscountBasisPoints / 100m);

            if (price < floor)
            {
                applied = CapLast(applied, floor - price);
                price = floor;
            }
        }

        return new PriceQuote(regular, price, applied);
    }

    // Takes the excess off the last rules applied, so the ones that ranked highest keep
    // their full effect and what the screen shows still adds up to the final price.
    private static List<AppliedDiscount> CapLast(List<AppliedDiscount> applied, Money excess)
    {
        var capped = new List<AppliedDiscount>(applied);

        for (var index = capped.Count - 1; index >= 0 && excess > Money.Zero; index--)
        {
            var cut = capped[index].Amount <= excess ? capped[index].Amount : excess;
            capped[index] = capped[index] with { Amount = capped[index].Amount - cut };
            excess -= cut;
        }

        return capped.Where(discount => discount.Amount > Money.Zero).ToList();
    }
}
