using Storage.Application.Catalog;

namespace Storage.Api.Endpoints;

public static class CategoryEndpoints
{
    /// <remarks>
    /// Changes are commands on their own routes (rename, move, activate) rather than one
    /// generic update: each carries a different rule - moving rewrites a whole branch -
    /// and the route says which one the caller means.
    /// </remarks>
    public static IEndpointRouteBuilder MapCategoryEndpoints(this IEndpointRouteBuilder app)
    {
        var categories = app.MapGroup("/api/categories").WithTags("Categories");

        categories.MapGet("/", (CategoryService service, CancellationToken cancellationToken) =>
            service.GetTreeAsync(cancellationToken));

        categories.MapPost("/", async (
            CreateCategoryRequest request,
            CategoryService service,
            CancellationToken cancellationToken) =>
        {
            var created = await service.CreateAsync(request, cancellationToken);
            return TypedResults.Created($"/api/categories/{created.Id}", created);
        });

        categories.MapPost("/{id:guid}/rename", (
            Guid id,
            RenameCategoryRequest request,
            CategoryService service,
            CancellationToken cancellationToken) =>
            service.RenameAsync(id, request, cancellationToken));

        categories.MapPost("/{id:guid}/move", (
            Guid id,
            MoveCategoryRequest request,
            CategoryService service,
            CancellationToken cancellationToken) =>
            service.MoveAsync(id, request, cancellationToken));

        categories.MapPost("/{id:guid}/activate", (
            Guid id,
            CategoryService service,
            CancellationToken cancellationToken) =>
            service.ActivateAsync(id, cancellationToken));

        categories.MapPost("/{id:guid}/deactivate", (
            Guid id,
            CategoryService service,
            CancellationToken cancellationToken) =>
            service.DeactivateAsync(id, cancellationToken));

        return app;
    }
}
