using Bipe.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Bipe.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    public const string DatabaseFileName = "bipe.db";

    /// <summary>
    /// Registers the local store. The caller decides where the file lives, so the
    /// infrastructure layer never reads configuration on its own.
    /// </summary>
    public static IServiceCollection AddBipePersistence(this IServiceCollection services, string dataDirectory)
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

        // A factory, not a scoped DbContext: a Blazor Server circuit lives as long as the
        // browser tab, and a context shared across that lifetime would hold every entity
        // it ever loaded and break under concurrent component renders.
        services.AddDbContextFactory<BipeDbContext>(options =>
            options.UseSqlite(connectionString)
                   .AddInterceptors(new SqlitePragmaInterceptor()));

        services.AddSingleton<SqliteStore>();

        return services;
    }
}
