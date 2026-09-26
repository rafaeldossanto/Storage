using Storage.Application.Abstractions;
using Storage.Application.Errors;
using Storage.Domain.Pricing;
using Storage.Domain.Stock;

namespace Storage.Application.Pricing;

/// <param name="Value">Basis points for a percentage (3000 is 30%), cents otherwise.</param>
public sealed record SaveDiscountRuleRequest(
    string Name,
    DiscountType Type,
    long Value,
    DiscountTarget TargetType,
    Guid TargetId,
    DateOnly? StartsOn = null,
    DateOnly? EndsOn = null,
    IReadOnlyList<DayOfWeek>? DaysOfWeek = null,
    int? ExpiringWithinDays = null,
    int Priority = 0,
    bool Stackable = false);

public sealed record DiscountRuleDto(
    Guid Id,
    string Name,
    DiscountType Type,
    long Value,
    DiscountTarget TargetType,
    Guid TargetId,
    DateOnly? StartsOn,
    DateOnly? EndsOn,
    IReadOnlyList<DayOfWeek> DaysOfWeek,
    int? ExpiringWithinDays,
    int Priority,
    bool Stackable,
    bool Active);

public sealed record DiscountPreviewDto(int ProductCount, IReadOnlyList<string> SampleNames);

public sealed record AppliedDiscountDto(Guid RuleId, string RuleName, long AmountCents);

/// <param name="PricedBatchExpiry">
/// Expiry of the batch that leaves next, which is what an expiry-window rule looks at. Null
/// when the product has no stock or does not expire.
/// </param>
public sealed record PriceQuoteDto(
    Guid ProductId,
    long RegularPriceCents,
    long FinalPriceCents,
    long DiscountCents,
    IReadOnlyList<AppliedDiscountDto> Applied,
    DateOnly? PricedBatchExpiry);

public sealed class PricingService(
    IDiscountRuleRepository rules,
    IProductRepository products,
    ICategoryRepository categories,
    IStockStore stock,
    IShopCalendar calendar,
    ITenantContext tenant)
{
    public const int PreviewSampleSize = 10;

    public async Task<IReadOnlyList<DiscountRuleDto>> ListAsync(CancellationToken cancellationToken = default) =>
        (await rules.ListAsync(cancellationToken))
            .OrderByDescending(rule => rule.Active)
            .ThenBy(rule => rule.Name, StringComparer.InvariantCultureIgnoreCase)
            .Select(ToDto)
            .ToArray();

    public async Task<DiscountRuleDto> CreateAsync(SaveDiscountRuleRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        await RequireTargetAsync(request.TargetType, request.TargetId, cancellationToken);

        var rule = DiscountRule.Create(
            tenant.TenantId, request.Name, request.Type, request.Value, request.TargetType, request.TargetId);
        Configure(rule, request);

        await rules.AddAsync(rule, cancellationToken);
        return ToDto(rule);
    }

    public async Task<DiscountRuleDto> UpdateAsync(
        Guid id,
        SaveDiscountRuleRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var rule = await RequireAsync(id, cancellationToken);
        await RequireTargetAsync(request.TargetType, request.TargetId, cancellationToken);

        rule.Rename(request.Name);
        rule.SetDiscount(request.Type, request.Value);
        rule.AimAt(request.TargetType, request.TargetId);
        Configure(rule, request);

        await rules.UpdateAsync(rule, cancellationToken);
        return ToDto(rule);
    }

    public async Task<DiscountRuleDto> ActivateAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var rule = await RequireAsync(id, cancellationToken);
        rule.Activate();

        await rules.UpdateAsync(rule, cancellationToken);
        return ToDto(rule);
    }

    public async Task<DiscountRuleDto> DeactivateAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var rule = await RequireAsync(id, cancellationToken);
        rule.Deactivate();

        await rules.UpdateAsync(rule, cancellationToken);
        return ToDto(rule);
    }

    /// <summary>
    /// How many products a rule on this target would reach, before saving it. A rule on a
    /// category reaches its whole branch, which is easy to underestimate: "this rule reaches
    /// 37 products" is what stops a discount from landing on things nobody meant.
    /// </summary>
    public async Task<DiscountPreviewDto> PreviewAsync(
        DiscountTarget targetType,
        Guid targetId,
        CancellationToken cancellationToken = default)
    {
        if (targetType == DiscountTarget.Product)
        {
            var product = await products.FindAsync(targetId, cancellationToken)
                ?? throw TargetNotFound();

            return new DiscountPreviewDto(1, [product.Name]);
        }

        var category = await categories.FindAsync(targetId, cancellationToken)
            ?? throw TargetNotFound();

        var reached = await products.ListByCategoryAsync(category, includeDescendants: true, cancellationToken);

        return new DiscountPreviewDto(
            reached.Count,
            reached.Select(product => product.Name).Take(PreviewSampleSize).ToArray());
    }

    /// <summary>
    /// What the product costs today: the regular price, every rule that reaches it, and the
    /// price that results. Priced on the batch that leaves next, since that is the unit a
    /// customer takes.
    /// </summary>
    public async Task<PriceQuoteDto> QuoteAsync(Guid productId, CancellationToken cancellationToken = default)
    {
        var product = await products.FindAsync(productId, cancellationToken)
            ?? throw UseCaseException.NotFound(ErrorCodes.ProductNotFound, $"Product {productId} does not exist.");

        var category = await categories.FindAsync(product.CategoryId, cancellationToken)
            ?? throw new InvalidOperationException($"Product {productId} points at a category that does not exist.");

        var nextBatch = Fefo.Order(await stock.ListBatchesAsync(productId, availableOnly: true, cancellationToken))
            .FirstOrDefault();

        var quote = DiscountEngine.Quote(
            product,
            category,
            await rules.ListActiveAsync(cancellationToken),
            await calendar.TodayAsync(cancellationToken),
            nextBatch?.ExpiryDate);

        return new PriceQuoteDto(
            product.Id,
            quote.RegularPrice.Cents,
            quote.FinalPrice.Cents,
            quote.Discount.Cents,
            quote.Applied.Select(applied => new AppliedDiscountDto(applied.RuleId, applied.RuleName, applied.Amount.Cents)).ToArray(),
            nextBatch?.ExpiryDate);
    }

    private static void Configure(DiscountRule rule, SaveDiscountRuleRequest request)
    {
        rule.Schedule(request.StartsOn, request.EndsOn, request.DaysOfWeek);
        rule.LimitToExpiringWithin(request.ExpiringWithinDays);
        rule.SetPriority(request.Priority);
        rule.SetStackable(request.Stackable);
    }

    // A rule aimed at something that is not in this shop would never apply - and would leak
    // whether an id from another shop exists if it were accepted.
    private async Task RequireTargetAsync(DiscountTarget targetType, Guid targetId, CancellationToken cancellationToken)
    {
        var exists = targetType switch
        {
            DiscountTarget.Product => await products.FindAsync(targetId, cancellationToken) is not null,
            DiscountTarget.Category => await categories.FindAsync(targetId, cancellationToken) is not null,
            _ => false,
        };

        if (!exists)
        {
            throw TargetNotFound();
        }
    }

    private async Task<DiscountRule> RequireAsync(Guid id, CancellationToken cancellationToken) =>
        await rules.FindAsync(id, cancellationToken)
        ?? throw UseCaseException.NotFound(ErrorCodes.DiscountNotFound, $"Discount rule {id} does not exist.");

    private static UseCaseException TargetNotFound() =>
        UseCaseException.NotFound(ErrorCodes.DiscountTargetNotFound, "The rule's target does not exist in this shop.");

    private static DiscountRuleDto ToDto(DiscountRule rule) => new(
        rule.Id,
        rule.Name,
        rule.Type,
        rule.Value,
        rule.TargetType,
        rule.TargetId,
        rule.StartsOn,
        rule.EndsOn,
        rule.DaysOfWeek,
        rule.ExpiringWithinDays,
        rule.Priority,
        rule.Stackable,
        rule.Active);
}
