using MongoDB.Driver;
using Storage.Application.Abstractions;
using Storage.Domain.Pricing;

namespace Storage.Infrastructure.Persistence;

public sealed class DiscountRuleRepository(MongoStorageContext context, ITenantContext tenant) : IDiscountRuleRepository
{
    private FilterDefinition<DiscountRule> OfThisShop =>
        Builders<DiscountRule>.Filter.Eq(rule => rule.TenantId, tenant.TenantId);

    public async Task<DiscountRule?> FindAsync(Guid id, CancellationToken cancellationToken = default) =>
        await context.DiscountRules
            .Find(OfThisShop & Builders<DiscountRule>.Filter.Eq(rule => rule.Id, id))
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<DiscountRule>> ListAsync(CancellationToken cancellationToken = default) =>
        await context.DiscountRules.Find(OfThisShop).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<DiscountRule>> ListActiveAsync(CancellationToken cancellationToken = default) =>
        await context.DiscountRules
            .Find(OfThisShop & Builders<DiscountRule>.Filter.Eq(rule => rule.Active, true))
            .ToListAsync(cancellationToken);

    public async Task AddAsync(DiscountRule rule, CancellationToken cancellationToken = default)
    {
        Guard(rule);
        await context.DiscountRules.InsertOneAsync(rule, options: null, cancellationToken);
    }

    public async Task UpdateAsync(DiscountRule rule, CancellationToken cancellationToken = default)
    {
        Guard(rule);

        await context.DiscountRules.ReplaceOneAsync(
            OfThisShop & Builders<DiscountRule>.Filter.Eq(stored => stored.Id, rule.Id),
            rule,
            new ReplaceOptions(),
            cancellationToken);
    }

    /// <summary>
    /// Refuses to write a record belonging to another shop, even if a caller hands one over.
    /// </summary>
    private void Guard(DiscountRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        if (rule.TenantId != tenant.TenantId)
        {
            throw new InvalidOperationException("Attempted to write a discount rule that belongs to another tenant.");
        }
    }
}
