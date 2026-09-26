using Microsoft.Extensions.DependencyInjection;
using Storage.Application.Catalog;

namespace Storage.Application;

public static class ApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddStorageApplication(this IServiceCollection services)
    {
        // Scoped, like the repositories they use: each request serves exactly one shop.
        services.AddScoped<CategoryService>();
        services.AddScoped<ProductService>();

        return services;
    }
}
