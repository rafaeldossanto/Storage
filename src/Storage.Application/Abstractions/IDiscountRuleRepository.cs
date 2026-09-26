using Storage.Domain.Pricing;

namespace Storage.Application.Abstractions;

/// <summary>The promotions of the current shop.</summary>
public interface IDiscountRuleRepository
{
    Task<DiscountRule?> FindAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DiscountRule>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Only the switched-on rules - the ones a price can depend on.</summary>
    Task<IReadOnlyList<DiscountRule>> ListActiveAsync(CancellationToken cancellationToken = default);

    Task AddAsync(DiscountRule rule, CancellationToken cancellationToken = default);

    Task UpdateAsync(DiscountRule rule, CancellationToken cancellationToken = default);
}
