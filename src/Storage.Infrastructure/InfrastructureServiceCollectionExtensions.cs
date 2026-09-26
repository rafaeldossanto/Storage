using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Storage.Infrastructure.Persistence;

namespace Storage.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    public const string DatabaseFileName = "storage.db";

    /// <summary>
    /// Registers the local store. The caller decides where the file lives, so the
    /// infrastructure layer never reads configuration on its own.
    /// </summary>
    public static IServiceCollection AddStoragePersistence(this IServiceCollection services, string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);

        Directory.CreateDirectory(dataDirectory);

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(dataDirectory, DatabaseFileName),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            ForeignKeys = true,
            // Waits for a busy writer instead of throwing SQLITE_BUSY at the cashier.
            DefaultTimeout = 30,
        }.ToString();

        services.TryAddTimeProvider();

        // A factory, not a scoped DbContext: a Blazor Server circuit lives as long as the
        // browser tab, and a context shared across that lifetime would hold every entity
        // it ever loaded and break under concurrent component renders.
        services.AddDbContextFactory<StorageDbContext>((provider, options) =>
            options.UseSqlite(connectionString)
                   .AddInterceptors(
                       new SqlitePragmaInterceptor(),
                       new TimestampInterceptor(provider.GetRequiredService<TimeProvider>())));

        services.AddSingleton<SqliteStore>();

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
