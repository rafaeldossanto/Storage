using Storage.Domain.Sales;
using Storage.Domain.ValueObjects;

namespace Storage.Application.Abstractions;

/// <summary>The sales of the current shop. They are written through the stock commit, with their stock.</summary>
public interface ISaleStore
{
    Task<Sale?> FindAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Completed sales in [<paramref name="from"/>, <paramref name="to"/>), summed by the
    /// database: in all, per bucket of time on the shop's clock, and per product.
    /// </summary>
    Task<SalesSummary> SummarizeAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        SalesBucket bucket,
        string timeZoneId,
        int topProducts,
        CancellationToken cancellationToken = default);
}

public enum SalesBucket
{
    Hour,
    Day,
    Month,
}

public sealed record SalesFigures(int Sales, int Units, Money Revenue, Money Cost)
{
    public static readonly SalesFigures None = new(0, 0, Money.Zero, Money.Zero);

    public Money Net => Revenue - Cost;
}

/// <param name="Start">When the bucket begins - local midnight, or the local hour - as an instant.</param>
public sealed record SalesBucketFigures(DateTimeOffset Start, SalesFigures Figures);

public sealed record SoldProductFigures(Guid ProductId, string Name, int Units, Money Revenue, Money Cost)
{
    public Money Net => Revenue - Cost;
}

public sealed record SalesSummary(
    SalesFigures Totals,
    IReadOnlyList<SalesBucketFigures> Buckets,
    IReadOnlyList<SoldProductFigures> Products);
