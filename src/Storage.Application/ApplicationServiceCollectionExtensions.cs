using Microsoft.Extensions.DependencyInjection;
using Storage.Application.Accounts;
using Storage.Application.Catalog;
using Storage.Application.Pricing;
using Storage.Application.Reports;
using Storage.Application.Sales;
using Storage.Application.Stock;

namespace Storage.Application;

public static class ApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddStorageApplication(this IServiceCollection services)
    {
        // Scoped, like the repositories they use: each request serves exactly one shop.
        services.AddScoped<CategoryService>();
        services.AddScoped<ProductService>();
        services.AddScoped<ProductPhotoService>();
        services.AddScoped<ProductDeletionService>();
        services.AddScoped<AuthService>();
        services.AddScoped<TeamService>();
        services.AddScoped<StockService>();
        services.AddScoped<ReceivingService>();
        services.AddScoped<StockQueries>();
        services.AddScoped<ExpiryService>();
        services.AddScoped<ReportsService>();
        services.AddScoped<CountService>();
        services.AddScoped<SalesService>();
        services.AddScoped<SalesReportService>();
        services.AddScoped<SalesAccessService>();
        services.AddScoped<PricingService>();
        services.AddScoped<SupplierService>();

        return services;
    }
}
