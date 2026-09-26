using Storage.Application.Pricing;
using Storage.Domain.Pricing;

namespace Storage.Api.Endpoints;

public static class DiscountEndpoints
{
    public static IEndpointRouteBuilder MapDiscountEndpoints(this IEndpointRouteBuilder app)
    {
        var discounts = app.MapGroup("/api/discounts").WithTags("Discounts");

        discounts.MapGet("/", (PricingService service, CancellationToken cancellationToken) =>
            service.ListAsync(cancellationToken));

        // Before saving: how many products a rule on this target would reach.
        discounts.MapGet("/preview", (
            DiscountTarget targetType,
            Guid targetId,
            PricingService service,
            CancellationToken cancellationToken) =>
            service.PreviewAsync(targetType, targetId, cancellationToken));

        discounts.MapPost("/", async (
            SaveDiscountRuleRequest request,
            PricingService service,
            CancellationToken cancellationToken) =>
        {
            var created = await service.CreateAsync(request, cancellationToken);
            return TypedResults.Created($"/api/discounts/{created.Id}", created);
        });

        discounts.MapPut("/{id:guid}", (
            Guid id,
            SaveDiscountRuleRequest request,
            PricingService service,
            CancellationToken cancellationToken) =>
            service.UpdateAsync(id, request, cancellationToken));

        discounts.MapPost("/{id:guid}/activate", (Guid id, PricingService service, CancellationToken cancellationToken) =>
            service.ActivateAsync(id, cancellationToken));

        discounts.MapPost("/{id:guid}/deactivate", (Guid id, PricingService service, CancellationToken cancellationToken) =>
            service.DeactivateAsync(id, cancellationToken));

        // The price a product sells for today, and which rules made it so.
        app.MapGet("/api/products/{productId:guid}/price", (
            Guid productId,
            PricingService service,
            CancellationToken cancellationToken) =>
            service.QuoteAsync(productId, cancellationToken))
            .WithTags("Discounts");

        return app;
    }
}
