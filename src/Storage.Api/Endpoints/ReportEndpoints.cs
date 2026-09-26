using Storage.Application.Reports;

namespace Storage.Api.Endpoints;

public static class ReportEndpoints
{
    public static IEndpointRouteBuilder MapReportEndpoints(this IEndpointRouteBuilder app)
    {
        // What expires in the next 30 days, soonest first, with the 3/7/15/30-day totals.
        app.MapGet("/api/stock/expiring", (ReportsService reports, CancellationToken cancellationToken) =>
            reports.ExpiringAsync(cancellationToken))
            .WithTags("Reports");

        // Losses between two dates on the shop's calendar, inclusive.
        app.MapGet("/api/reports/losses", (
            DateOnly from,
            DateOnly to,
            ReportsService reports,
            CancellationToken cancellationToken) =>
            reports.LossesAsync(from, to, cancellationToken))
            .WithTags("Reports");

        return app;
    }
}
