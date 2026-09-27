using Microsoft.Net.Http.Headers;
using Storage.Application.Catalog;

namespace Storage.Api.Endpoints;

public static class ProductPhotoEndpoints
{
    public const string RateLimitPolicy = "product-photos";

    public static IEndpointRouteBuilder MapProductPhotoEndpoints(this IEndpointRouteBuilder app)
    {
        // Open, unlike every other route: an <img> tag cannot send the access token, and a
        // photo of a public product package tells nobody anything about a shop.
        app.MapGet($"{ProductPhotoDto.Route}/{{gtin}}", async (
                string gtin,
                string? v,
                ProductPhotoService photos,
                HttpContext http,
                CancellationToken cancellationToken) =>
            {
                var file = await photos.OpenAsync(gtin, cancellationToken);

                if (file is null)
                {
                    return Results.NotFound();
                }

                // The address a product carries names the version: that one never changes,
                // so browsers keep it for a year. Any other address may meet a new picture.
                http.Response.Headers.CacheControl = v == file.Version
                    ? "public, max-age=31536000, immutable"
                    : "public, max-age=300";
                http.Response.Headers.XContentTypeOptions = "nosniff";

                return Results.File(file.Content, file.ContentType, entityTag: new EntityTagHeaderValue($"\"{file.Version}\""));
            })
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicy)
            .WithTags("Products")
            .Produces(StatusCodes.Status200OK, contentType: "image/webp")
            .Produces(StatusCodes.Status404NotFound);

        return app;
    }
}
