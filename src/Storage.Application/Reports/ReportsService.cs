using Storage.Application.Abstractions;
using Storage.Application.Errors;
using Storage.Domain.Catalog;
using Storage.Domain.Stock;
using Storage.Domain.ValueObjects;

namespace Storage.Application.Reports;

public sealed record ExpiringBatchDto(
    Guid BatchId,
    Guid ProductId,
    string ProductName,
    DateOnly ExpiryDate,
    int DaysLeft,
    int Quantity,
    long ValueCents);

/// <summary>What is at risk within a window: units and money, at cost.</summary>
public sealed record ExpiryWindowDto(int WithinDays, int Units, long ValueCents);

public sealed record ExpiryDashboardDto(
    DateOnly Today,
    IReadOnlyList<ExpiryWindowDto> Windows,
    IReadOnlyList<ExpiringBatchDto> Batches);

public sealed record LossByTypeDto(MovementType Type, int Units, long ValueCents);

public sealed record LossByCategoryDto(Guid CategoryId, string CategoryName, int Units, long ValueCents);

public sealed record LossByProductDto(Guid ProductId, string ProductName, int Units, long ValueCents);

public sealed record LossReportDto(
    DateOnly From,
    DateOnly To,
    int Units,
    long ValueCents,
    IReadOnlyList<LossByTypeDto> ByType,
    IReadOnlyList<LossByCategoryDto> ByCategory,
    IReadOnlyList<LossByProductDto> TopProducts);

/// <summary>
/// What the shop is about to lose and what it has lost - the two numbers that make it buy
/// better next month.
/// </summary>
public sealed class ReportsService(
    IStockStore stock,
    IProductRepository products,
    ICategoryRepository categories,
    IShopCalendar calendar)
{
    /// <summary>The cards on the expiry dashboard: 3, 7, 15 and 30 days ahead.</summary>
    public static readonly int[] ExpiryWindows = [3, 7, 15, 30];

    public const int MaxReportDays = 366;
    public const int TopProductsCount = 10;

    /// <summary>What expires in the next 30 days, soonest first, with what it cost.</summary>
    public async Task<ExpiryDashboardDto> ExpiringAsync(CancellationToken cancellationToken = default)
    {
        var today = await calendar.TodayAsync(cancellationToken);
        var horizon = ExpiryWindows.Max();

        var batches = await stock.ListExpiringBatchesAsync(today, today.AddDays(horizon), cancellationToken);
        var names = await ProductNamesAsync(batches.Select(batch => batch.ProductId), cancellationToken);

        var items = batches
            .Where(batch => batch.IsAvailable && batch.ExpiryDate is not null)
            .Select(batch => new ExpiringBatchDto(
                batch.Id,
                batch.ProductId,
                names.GetValueOrDefault(batch.ProductId, string.Empty),
                batch.ExpiryDate!.Value,
                batch.ExpiryDate.Value.DayNumber - today.DayNumber,
                batch.RemainingQuantity,
                batch.Value.Cents))
            .OrderBy(item => item.ExpiryDate)
            .ThenBy(item => item.ProductName, StringComparer.InvariantCultureIgnoreCase)
            .ToArray();

        var windows = ExpiryWindows
            .Select(days =>
            {
                var inWindow = items.Where(item => item.DaysLeft <= days).ToArray();
                return new ExpiryWindowDto(days, inWindow.Sum(item => item.Quantity), inWindow.Sum(item => item.ValueCents));
            })
            .ToArray();

        return new ExpiryDashboardDto(today, windows, items);
    }

    /// <summary>
    /// Losses between two dates on the shop's calendar, inclusive: expired and damaged goods,
    /// by type, by top-level category and by product, valued at what they cost.
    /// </summary>
    public async Task<LossReportDto> LossesAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        if (to < from || to.DayNumber - from.DayNumber >= MaxReportDays)
        {
            throw UseCaseException.Invalid(
                ErrorCodes.ReportPeriodInvalid, $"A report covers from 1 to {MaxReportDays} days, start before end.");
        }

        var start = await calendar.StartOfDayAsync(from, cancellationToken);
        var end = await calendar.StartOfDayAsync(to.AddDays(1), cancellationToken);

        // Returns to the supplier are not losses: the supplier usually credits them.
        var totals = await stock.SumMovementsAsync(
            start, end, [MovementType.ExpiryLoss, MovementType.DamageLoss], cancellationToken);

        var productIds = totals.Select(total => total.ProductId).Distinct().ToArray();
        var listed = (await products.ListByIdsAsync(productIds, cancellationToken)).ToDictionary(product => product.Id);
        var roots = RootsOf(await categories.ListAsync(cancellationToken));

        // Movements out are negative; a loss report speaks in positive amounts.
        var losses = totals
            .Select(total => (total.ProductId, total.Type, Units: -total.Quantity, Value: -total.Value))
            .ToArray();

        var byType = losses
            .GroupBy(loss => loss.Type)
            .Select(group => new LossByTypeDto(group.Key, group.Sum(loss => loss.Units), Sum(group.Select(loss => loss.Value))))
            .OrderByDescending(loss => loss.ValueCents)
            .ToArray();

        var byProduct = losses
            .GroupBy(loss => loss.ProductId)
            .Select(group => new LossByProductDto(
                group.Key,
                listed.TryGetValue(group.Key, out var product) ? product.Name : string.Empty,
                group.Sum(loss => loss.Units),
                Sum(group.Select(loss => loss.Value))))
            .OrderByDescending(loss => loss.ValueCents)
            .ToArray();

        var byCategory = losses
            .GroupBy(loss => listed.TryGetValue(loss.ProductId, out var product) && roots.TryGetValue(product.CategoryId, out var root)
                ? root
                : (Id: Guid.Empty, Name: string.Empty))
            .Select(group => new LossByCategoryDto(
                group.Key.Id, group.Key.Name, group.Sum(loss => loss.Units), Sum(group.Select(loss => loss.Value))))
            .OrderByDescending(loss => loss.ValueCents)
            .ToArray();

        return new LossReportDto(
            from,
            to,
            losses.Sum(loss => loss.Units),
            Sum(losses.Select(loss => loss.Value)),
            byType,
            byCategory,
            byProduct.Take(TopProductsCount).ToArray());
    }

    private async Task<Dictionary<Guid, string>> ProductNamesAsync(IEnumerable<Guid> ids, CancellationToken cancellationToken) =>
        (await products.ListByIdsAsync(ids.Distinct().ToArray(), cancellationToken))
            .ToDictionary(product => product.Id, product => product.Name);

    // Each category mapped to the top of its branch: a loss report reads best by the
    // sections a shop is organised in - Beverages, Grocery, Cleaning.
    private static Dictionary<Guid, (Guid Id, string Name)> RootsOf(IReadOnlyList<Category> all)
    {
        var byId = all.ToDictionary(category => category.Id);

        return all.ToDictionary(
            category => category.Id,
            category =>
            {
                var rootId = category.SelfAndAncestorIds[0];
                return byId.TryGetValue(rootId, out var root) ? (root.Id, root.Name) : (category.Id, category.Name);
            });
    }

    private static long Sum(IEnumerable<Money> values) => values.Aggregate(Money.Zero, (sum, value) => sum + value).Cents;
}
