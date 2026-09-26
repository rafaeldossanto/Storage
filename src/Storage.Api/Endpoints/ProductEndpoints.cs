using Storage.Application.Catalog;

namespace Storage.Api.Endpoints;

public static class ProductEndpoints
{
    public static IEndpointRouteBuilder MapProductEndpoints(this IEndpointRouteBuilder app)
    {
        var products = app.MapGroup("/api/products").WithTags("Products");

        // Either a name search or a category listing. The catalogue of a shop runs to
        // thousands of products, so there is deliberately no "give me everything".
        products.MapGet("/", async (
            string? search,
            Guid? categoryId,
            bool? includeDescendants,
            ProductService service,
            CancellationToken cancellationToken) =>
        {
            if (!string.IsNullOrWhiteSpace(search))
            {
                return await service.SearchAsync(search, cancellationToken);
            }

            if (categoryId is { } category)
            {
                return await service.ListByCategoryAsync(
                    category, includeDescendants ?? true, cancellationToken);
            }

            return [];
        });

        products.MapGet("/{id:guid}", (
            Guid id,
            ProductService service,
            CancellationToken cancellationToken) =>
            service.GetAsync(id, cancellationToken));

        // A 404 here is the normal path for a new product: the screen takes it as the cue
        // to open the registration form with the scanned code already filled in.
        products.MapGet("/by-barcode/{barcode}", (
            string barcode,
            ProductService service,
            CancellationToken cancellationToken) =>
            service.FindByBarcodeAsync(barcode, cancellationToken));

        products.MapPost("/", async (
            CreateProductRequest request,
            ProductService service,
            CancellationToken cancellationToken) =>
        {
            var created = await service.CreateAsync(request, cancellationToken);
            return TypedResults.Created($"/api/products/{created.Id}", created);
        });

        products.MapPut("/{id:guid}", (
            Guid id,
            UpdateProductRequest request,
            ProductService service,
            CancellationToken cancellationToken) =>
            service.UpdateAsync(id, request, cancellationToken));

        products.MapPost("/{id:guid}/packagings", (
            Guid id,
            AddPackagingRequest request,
            ProductService service,
            CancellationToken cancellationToken) =>
            service.AddPackagingAsync(id, request, cancellationToken));

        products.MapDelete("/{id:guid}/packagings/{packagingId:guid}", (
            Guid id,
            Guid packagingId,
            ProductService service,
            CancellationToken cancellationToken) =>
            service.RemovePackagingAsync(id, packagingId, cancellationToken));

        products.MapPost("/{id:guid}/activate", (
            Guid id,
            ProductService service,
            CancellationToken cancellationToken) =>
            service.ActivateAsync(id, cancellationToken));

        products.MapPost("/{id:guid}/deactivate", (
            Guid id,
            ProductService service,
            CancellationToken cancellationToken) =>
            service.DeactivateAsync(id, cancellationToken));

        return app;
    }
}
