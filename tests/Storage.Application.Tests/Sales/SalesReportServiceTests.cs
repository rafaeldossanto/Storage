using Storage.Application.Abstractions;
using Storage.Application.Errors;
using Storage.Application.Sales;
using Storage.Application.Tests.Fakes;
using Storage.Domain.ValueObjects;

namespace Storage.Application.Tests.Sales;

public sealed class SalesReportServiceTests
{
    private static readonly Guid Shop = Guid.CreateVersion7();

    private readonly InMemorySaleStore _sales;
    private readonly SalesReportService _service;

    public SalesReportServiceTests()
    {
        var tenant = new FixedTenant(Shop);
        _sales = new InMemorySaleStore(new InMemoryStockStore(Shop), tenant);
        _service = new SalesReportService(_sales, new FixedCalendar(new DateOnly(2026, 9, 26)));
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static SalesFigures Selling(long revenue, long cost) => new(1, 1, Money.FromCents(revenue), Money.FromCents(cost));

    [Fact]
    public async Task A_day_has_all_twenty_four_hours_even_the_quiet_ones()
    {
        _sales.Summary = new SalesSummary(
            Selling(1000, 600),
            [new SalesBucketFigures(new DateTimeOffset(2026, 9, 26, 14, 0, 0, TimeSpan.Zero), Selling(1000, 600))],
            []);

        var report = await _service.ReportAsync(SalesPeriod.Day, new DateOnly(2026, 9, 26), Token);

        Assert.Equal(24, report.Buckets.Count);
        var afternoon = Assert.Single(report.Buckets, bucket => bucket.RevenueCents > 0);
        Assert.Equal(new DateTime(2026, 9, 26, 14, 0, 0), afternoon.Start);
        Assert.Equal(400, afternoon.NetCents);
        Assert.Equal(400, report.Totals.NetCents);
    }

    [Fact]
    public async Task A_month_has_a_bucket_per_day_and_asks_for_the_whole_month()
    {
        var report = await _service.ReportAsync(SalesPeriod.Month, new DateOnly(2026, 9, 26), Token);

        Assert.Equal(30, report.Buckets.Count);
        Assert.Equal(new DateOnly(2026, 9, 1), report.From);
        Assert.Equal(new DateOnly(2026, 9, 30), report.To);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), _sales.LastQuery!.Value.To);
        Assert.Equal(SalesBucket.Day, _sales.LastQuery.Value.Bucket);
    }

    [Fact]
    public async Task A_year_has_twelve_months()
    {
        var report = await _service.ReportAsync(SalesPeriod.Year, new DateOnly(2026, 9, 26), Token);

        Assert.Equal(12, report.Buckets.Count);
        Assert.Equal(new DateOnly(2026, 12, 31), report.To);
        Assert.Equal(SalesBucket.Month, _sales.LastQuery!.Value.Bucket);
    }

    [Fact]
    public async Task A_date_out_of_range_is_refused()
    {
        await Refused.WithAsync(
            ErrorKind.Invalid, ErrorCodes.SalesPeriodInvalid, () => _service.ReportAsync(SalesPeriod.Day, new DateOnly(1999, 1, 1), Token));
    }
}
