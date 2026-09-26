using Storage.Api.Auth;
using Storage.Api.Errors;
using Storage.Application.Abstractions;
using Storage.Application.Sales;

namespace Storage.Api.Endpoints;

public static class SalesEndpoints
{
    /// <summary>The code for a sales report asked for without a valid pass: the PIN is needed.</summary>
    public const string LockedCode = "sales.locked";

    public static IEndpointRouteBuilder MapSalesEndpoints(this IEndpointRouteBuilder app)
    {
        var sales = app.MapGroup("/api/sales").WithTags("Sales");

        // Ringing up a sale is for anyone in the shop; what the sales were worth is not.
        sales.MapPost("/", async (RegisterSaleRequest request, SalesService service, CancellationToken cancellationToken) =>
        {
            var sale = await service.RegisterAsync(request, cancellationToken);
            return TypedResults.Created($"/api/sales/{sale.Id}", sale);
        });

        // The undo right after a wrong scan; refused once the sale is ten minutes old.
        sales.MapPost("/{id:guid}/cancel", (Guid id, SalesService service, CancellationToken cancellationToken) =>
            service.CancelAsync(id, cancellationToken));

        sales.MapGet("/pin", (SalesAccessService service, CancellationToken cancellationToken) =>
            service.StatusAsync(cancellationToken));

        sales.MapPut("/pin", async (SalesPinRequest request, SalesAccessService service, CancellationToken cancellationToken) =>
        {
            await service.SetPinAsync(request, cancellationToken);
            return TypedResults.NoContent();
        })
        .RequireAuthorization(TeamEndpoints.OwnerPolicy);

        // Rate-limited like signing in: it is the route a PIN-guessing script would hammer.
        sales.MapPost("/unlock", (SalesPinRequest request, SalesAccessService service, CancellationToken cancellationToken) =>
            service.UnlockAsync(request, cancellationToken))
            .RequireRateLimiting(AuthEndpoints.RateLimitPolicy);

        sales.MapGet("/report", (
            SalesPeriod period,
            DateOnly date,
            SalesReportService service,
            CancellationToken cancellationToken) =>
            service.ReportAsync(period, date, cancellationToken))
            .AddEndpointFilter<SalesAccessFilter>();

        return app;
    }
}

/// <summary>Lets a request through only with a current sales pass, issued to this person.</summary>
internal sealed class SalesAccessFilter(SalesAccessTokens tokens, ITenantContext tenant, ICurrentUser user) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var pass = context.HttpContext.Request.Headers[SalesAccessTokens.Header].ToString();

        if (!await tokens.IsValidAsync(pass, tenant.TenantId, user.UserId))
        {
            await ProblemExceptionHandler.WriteAsync(context.HttpContext, StatusCodes.Status403Forbidden, SalesEndpoints.LockedCode);
            return Results.Empty;
        }

        return await next(context);
    }
}
