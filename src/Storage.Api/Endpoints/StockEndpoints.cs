using Storage.Application.Abstractions;
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
            int? page,
            int? pageSize,
            StockQueries queries,
            CancellationToken cancellationToken) =>
            queries.ListByCategoryAsync(
                categoryId, includeDescendants ?? true, PageRequest.Of(page, pageSize), cancellationToken));

        // What is running out - the list to take to the supplier.
        stock.MapGet("/below-minimum", (int? page, int? pageSize, StockQueries queries, CancellationToken cancellationToken) =>
            queries.ListBelowMinimumAsync(PageRequest.Of(page, pageSize), cancellationToken));

        // Runs the expiry sweep for this shop now instead of waiting for the next scheduled run.
        stock.MapPost("/expiry-sweep", (ExpiryService service, CancellationToken cancellationToken) =>
            service.ExpireDueAsync(cancellationToken))
            .RequireAuthorization(TeamEndpoints.OwnerPolicy);

        stock.MapGet("/summary", (StockQueries queries, CancellationToken cancellationToken) =>
            queries.SummaryAsync(cancellationToken));

        // One product: its batches in the order they will leave, and its recent history.
        stock.MapGet("/products/{productId:guid}", (
            Guid productId,
            StockQueries queries,
            CancellationToken cancellationToken) =>
            queries.GetProductAsync(productId, cancellationToken));

        // The product's whole ledger, newest first - the product view above only shows the latest.
        stock.MapGet("/products/{productId:guid}/movements", (
            Guid productId,
            int? page,
            int? pageSize,
            StockQueries queries,
            CancellationToken cancellationToken) =>
            queries.ListMovementsAsync(productId, PageRequest.Of(page, pageSize), cancellationToken));

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
