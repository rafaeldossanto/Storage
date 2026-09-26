using Storage.Application.Stock;

namespace Storage.Api.Endpoints;

public static class StockEndpoints
{
    public static IEndpointRouteBuilder MapStockEndpoints(this IEndpointRouteBuilder app)
    {
        var stock = app.MapGroup("/api/stock").WithTags("Stock");

        // By category, with its whole branch unless told otherwise.
        stock.MapGet("/", (
            Guid categoryId,
            bool? includeDescendants,
            StockQueries queries,
            CancellationToken cancellationToken) =>
            queries.ListByCategoryAsync(categoryId, includeDescendants ?? true, cancellationToken));

        // What is running out - the list to take to the supplier.
        stock.MapGet("/below-minimum", (StockQueries queries, CancellationToken cancellationToken) =>
            queries.ListBelowMinimumAsync(cancellationToken));

        stock.MapGet("/summary", (StockQueries queries, CancellationToken cancellationToken) =>
            queries.SummaryAsync(cancellationToken));

        // One product: its batches in the order they will leave, and its recent history.
        stock.MapGet("/products/{productId:guid}", (
            Guid productId,
            StockQueries queries,
            CancellationToken cancellationToken) =>
            queries.GetProductAsync(productId, cancellationToken));

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
