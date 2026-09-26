using Storage.Application.Abstractions;
using Storage.Application.Stock;

namespace Storage.Api.Endpoints;

public static class ReceivingEndpoints
{
    public static IEndpointRouteBuilder MapReceivingEndpoints(this IEndpointRouteBuilder app)
    {
        var receipts = app.MapGroup("/api/receipts").WithTags("Receiving");

        // A whole delivery in one request, committed atomically: a refused line - with its
        // number in the problem's "line" field - leaves nothing half-received.
        receipts.MapPost("/", async (
            ReceiveGoodsRequest request,
            ReceivingService service,
            CancellationToken cancellationToken) =>
        {
            var received = await service.ReceiveAsync(request, cancellationToken);
            return TypedResults.Created($"/api/receipts/{received.Id}", received);
        });

        receipts.MapGet("/", (int? page, int? pageSize, ReceivingService service, CancellationToken cancellationToken) =>
            service.ListAsync(PageRequest.Of(page, pageSize), cancellationToken));

        receipts.MapGet("/{id:guid}", (Guid id, ReceivingService service, CancellationToken cancellationToken) =>
            service.GetAsync(id, cancellationToken));

        // The undo for a receipt typed wrong: ten minutes, and only while its goods are all
        // still on the shelf.
        receipts.MapPost("/{id:guid}/cancel", (Guid id, ReceivingService service, CancellationToken cancellationToken) =>
            service.CancelAsync(id, cancellationToken));

        var suppliers = app.MapGroup("/api/suppliers").WithTags("Suppliers");

        suppliers.MapGet("/", (SupplierService service, CancellationToken cancellationToken) =>
            service.ListAsync(cancellationToken));

        suppliers.MapPost("/", async (
            SaveSupplierRequest request,
            SupplierService service,
            CancellationToken cancellationToken) =>
        {
            var created = await service.CreateAsync(request, cancellationToken);
            return TypedResults.Created($"/api/suppliers/{created.Id}", created);
        });

        suppliers.MapPut("/{id:guid}", (
            Guid id,
            SaveSupplierRequest request,
            SupplierService service,
            CancellationToken cancellationToken) =>
            service.UpdateAsync(id, request, cancellationToken));

        suppliers.MapPost("/{id:guid}/activate", (Guid id, SupplierService service, CancellationToken cancellationToken) =>
            service.ActivateAsync(id, cancellationToken));

        suppliers.MapPost("/{id:guid}/deactivate", (Guid id, SupplierService service, CancellationToken cancellationToken) =>
            service.DeactivateAsync(id, cancellationToken));

        return app;
    }
}
