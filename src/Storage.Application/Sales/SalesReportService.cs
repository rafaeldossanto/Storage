using Storage.Application.Abstractions;
using Storage.Application.Errors;
using Storage.Domain.ValueObjects;

namespace Storage.Application.Sales;

public enum SalesPeriod
{
    Day,
    Month,
    Year,
}

public sealed record SalesTotalsDto(int Sales, int Units, long RevenueCents, long CostCents, long NetCents);

/// <param name="Start">
/// Where the bucket begins on the shop's clock: the hour for a day, the day for a month, the
/// first of the month for a year.
/// </param>
public sealed record SalesBucketDto(DateTime Start, int Sales, long RevenueCents, long CostCents, long NetCents);

public sealed record SoldProductDto(Guid ProductId, string Name, int Units, long RevenueCents, long CostCents, long NetCents);

/// <param name="To">The last day the report covers, inclusive.</param>
public sealed record SalesReportDto(
    SalesPeriod Period,
    DateOnly From,
    DateOnly To,
    SalesTotalsDto Totals,
    IReadOnlyList<SalesBucketDto> Buckets,
    IReadOnlyList<SoldProductDto> Products);

/// <summary>
/// What was sold in a day, a month or a year, what it had cost, and what was left - by
/// hour, by day or by month, and per product.
/// </summary>
public sealed class SalesReportService(ISaleStore sales, IShopCalendar calendar)
{
    /// <summary>The products listed, best sellers first.</summary>
    public const int TopProducts = 100;

    public async Task<SalesReportDto> ReportAsync(
        SalesPeriod period,
        DateOnly date,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(period) || date.Year is < 2000 or > 2100)
        {
            throw UseCaseException.Invalid(ErrorCodes.SalesPeriodInvalid, "Pick a day, a month or a year between 2000 and 2100.");
        }

        var (from, to, bucket) = period switch
        {
            SalesPeriod.Day => (date, date.AddDays(1), SalesBucket.Hour),
            SalesPeriod.Month => (new DateOnly(date.Year, date.Month, 1), new DateOnly(date.Year, date.Month, 1).AddMonths(1), SalesBucket.Day),
            _ => (new DateOnly(date.Year, 1, 1), new DateOnly(date.Year + 1, 1, 1), SalesBucket.Month),
        };

        var timeZoneId = await calendar.TimeZoneIdAsync(cancellationToken);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);

        var summary = await sales.SummarizeAsync(
            await calendar.StartOfDayAsync(from, cancellationToken),
            await calendar.StartOfDayAsync(to, cancellationToken),
            bucket,
            timeZoneId,
            TopProducts,
            cancellationToken);

        // The database only returns buckets that had sales; the chart wants every hour of
        // the day, every day of the month, every month of the year - quiet ones as zero.
        var found = summary.Buckets.ToDictionary(figures => figures.Start.UtcDateTime, figures => figures.Figures);

        var buckets = BucketStarts(from, to, bucket)
            .Select(local =>
            {
                var figures = TryUtc(local, zone) is { } utc && found.TryGetValue(utc, out var hit) ? hit : SalesFigures.None;
                return new SalesBucketDto(local, figures.Sales, figures.Revenue.Cents, figures.Cost.Cents, figures.Net.Cents);
            })
            .ToArray();

        return new SalesReportDto(
            period,
            from,
            to.AddDays(-1),
            ToDto(summary.Totals),
            buckets,
            summary.Products
                .Select(product => new SoldProductDto(
                    product.ProductId, product.Name, product.Units, product.Revenue.Cents, product.Cost.Cents, product.Net.Cents))
                .ToArray());
    }

    private static IEnumerable<DateTime> BucketStarts(DateOnly from, DateOnly to, SalesBucket bucket)
    {
        var start = from.ToDateTime(TimeOnly.MinValue);
        var end = to.ToDateTime(TimeOnly.MinValue);

        for (var local = start; local < end; local = Next(local, bucket))
        {
            yield return local;
        }
    }

    private static DateTime Next(DateTime local, SalesBucket bucket) => bucket switch
    {
        SalesBucket.Hour => local.AddHours(1),
        SalesBucket.Day => local.AddDays(1),
        _ => local.AddMonths(1),
    };

    // A local hour that does not exist - skipped when clocks go forward - has no sales.
    private static DateTime? TryUtc(DateTime local, TimeZoneInfo zone) =>
        zone.IsInvalidTime(local) ? null : TimeZoneInfo.ConvertTimeToUtc(local, zone);

    private static SalesTotalsDto ToDto(SalesFigures figures) =>
        new(figures.Sales, figures.Units, figures.Revenue.Cents, figures.Cost.Cents, figures.Net.Cents);
}
