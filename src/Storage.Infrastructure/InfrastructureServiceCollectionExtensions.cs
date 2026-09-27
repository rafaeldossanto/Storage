using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Storage.Application.Abstractions;
using Storage.Infrastructure.Persistence;
using Storage.Infrastructure.Photos;
using Storage.Infrastructure.Security;

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
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IStockStore, MongoStockStore>();
        services.AddScoped<ISupplierRepository, SupplierRepository>();
        services.AddScoped<IDiscountRuleRepository, DiscountRuleRepository>();
        services.AddScoped<ICountStore, MongoCountStore>();
        services.AddScoped<ISaleStore, MongoSaleStore>();
        services.AddScoped<IShopCalendar, ShopCalendar>();

        // Not tenant-scoped by design: the photo of a barcode is the same in every shop.
        services.AddSingleton<IProductPhotoStore, MongoProductPhotoStore>();

        // Not tenant-scoped by design: sign-in runs before the shop is known.
        services.AddScoped<IAccountStore, MongoAccountStore>();
        services.AddScoped<ITenantDirectory, MongoTenantDirectory>();

        // Singleton so the decoy hash used to hide which e-mails exist is computed once.
        services.AddSingleton<IPasswordHasher, IdentityPasswordHasher>();

        return services;
    }

    /// <summary>
    /// Registers where product photos are looked up and how they are cut out. The caller
    /// supplies the options, read from its configuration.
    /// </summary>
    public static IServiceCollection AddStorageProductPhotos(this IServiceCollection services, ProductPhotoOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        services.AddSingleton(options);

        services.AddHttpClient<IProductPhotoSource, OpenFoodFactsPhotoSource>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(20);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(options.UserAgent);
        });

        // The model when it is on disk; otherwise only photos shot on a plain backdrop get
        // cut out, and the rest are placed on white whole.
        services.AddSingleton<IBackgroundCutter>(_ =>
            options.CutoutModelPath is { } model && File.Exists(model)
                ? new IsnetBackgroundCutter(model)
                : new SolidBackdropCutter());

        services.AddSingleton<IPackshotStudio, PackshotStudio>();

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
