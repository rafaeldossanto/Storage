using Storage.Application.Stock;

namespace Storage.Api.Endpoints;

public static class StockEndpoints
{
    public static IEndpointRouteBuilder MapStockEndpoints(this IEndpointRouteBuilder app)
    {
        var stock = app.MapGroup("/api/stock").WithTags("Stock");

        // Damage or a return to the supplier. Expiry is never recorded by hand: the daily
        // job does it, so every expired unit is counted exactly once.
        stock.MapPost("/products/{productId:guid}/removals", (
            Guid productId,
            RemoveStockRequest request,
            StockService service,
            CancellationToken cancellationToken) =>
            service.RemoveAsync(productId, request, cancellationToken));

        return app;
    }
}
