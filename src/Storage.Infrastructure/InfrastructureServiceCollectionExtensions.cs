using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Storage.Application.Abstractions;
using Storage.Infrastructure.Persistence;

namespace Storage.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    /// <summary>
    /// Registers the MongoDB store. The caller supplies the connection, so the
    /// infrastructure layer never reads configuration on its own.
    /// </summary>
    public static IServiceCollection AddStoragePersistence(
        this IServiceCollection services,
        string connectionString,
        string databaseName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);

        services.TryAddTimeProvider();

        // The driver's client is thread-safe and owns the connection pool, so exactly one
        // instance serves the whole application - a new client per request would open a
        // new pool each time.
        services.AddSingleton<IMongoClient>(_ => new MongoClient(connectionString));

        services.AddSingleton(provider =>
            new MongoStorageContext(provider.GetRequiredService<IMongoClient>(), databaseName));

        // Scoped: a repository reads the tenant of the request it is serving.
        services.AddScoped<ICategoryRepository, CategoryRepository>();
        services.AddScoped<IProductRepository, ProductRepository>();

        return services;
    }

    private static void TryAddTimeProvider(this IServiceCollection services)
    {
        if (services.All(descriptor => descriptor.ServiceType != typeof(TimeProvider)))
        {
            services.AddSingleton(TimeProvider.System);
        }
    }
}
