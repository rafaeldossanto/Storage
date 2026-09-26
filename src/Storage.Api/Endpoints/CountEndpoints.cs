using Storage.Application.Abstractions;
using Storage.Application.Stock;

namespace Storage.Api.Endpoints;

public static class CountEndpoints
{
    public static IEndpointRouteBuilder MapCountEndpoints(this IEndpointRouteBuilder app)
    {
        var counts = app.MapGroup("/api/counts").WithTags("Counts");

        counts.MapGet("/", (int? page, int? pageSize, CountService service, CancellationToken cancellationToken) =>
            service.ListAsync(PageRequest.Of(page, pageSize), cancellationToken));

        counts.MapPost("/", async (StartCountRequest request, CountService service, CancellationToken cancellationToken) =>
        {
            var started = await service.StartAsync(request, cancellationToken);
            return TypedResults.Created($"/api/counts/{started.Id}", started);
        });

        // While open, each item shows what the ledger says now, so differences show before closing.
        counts.MapGet("/{id:guid}", (Guid id, CountService service, CancellationToken cancellationToken) =>
            service.GetAsync(id, cancellationToken));

        // Anyone in the shop counts: from the phone by scanning, or by typing a quantity.
        counts.MapPost("/{id:guid}/scans", (
            Guid id,
            ScanRequest request,
            CountService service,
            CancellationToken cancellationToken) =>
            service.ScanAsync(id, request, cancellationToken));

        counts.MapPut("/{id:guid}/items/{productId:guid}", (
            Guid id,
            Guid productId,
            SetCountedRequest request,
            CountService service,
            CancellationToken cancellationToken) =>
            service.SetCountedAsync(id, productId, request, cancellationToken));

        // Closing rewrites the ledger, so only the owner does it - an adjustment is where
        // shrinkage would otherwise be hidden.
        counts.MapPost("/{id:guid}/close", (
            Guid id,
            CloseCountRequest request,
            CountService service,
            CancellationToken cancellationToken) =>
            service.CloseAsync(id, request, cancellationToken))
            .RequireAuthorization(TeamEndpoints.OwnerPolicy);

        counts.MapPost("/{id:guid}/cancel", (Guid id, CountService service, CancellationToken cancellationToken) =>
            service.CancelAsync(id, cancellationToken))
            .RequireAuthorization(TeamEndpoints.OwnerPolicy);

        return app;
    }
}
