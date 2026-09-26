using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Storage.Domain.Catalog;
using Storage.Domain.ValueObjects;
using Storage.Infrastructure;
using Storage.Infrastructure.Persistence;

namespace Storage.Integration.Tests.Persistence;

/// <summary>
/// Exercises a real SQLite file through the real service registration, because the
/// decisions worth testing here - cents in an INTEGER column, a barcode unique across the
/// shop, write-ahead logging - only exist once the provider is involved.
/// </summary>
public sealed class CatalogPersistenceTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 9, 30, 0, TimeSpan.Zero);

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "storage-tests", Guid.CreateVersion7().ToString("N"));

    private ServiceProvider _provider = null!;
    private IDbContextFactory<StorageDbContext> _contextFactory = null!;

    /// <summary>Cancels with the test run instead of hanging a stuck connection.</summary>
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(new FixedClock(Now));
        services.AddStoragePersistence(_directory);

        _provider = services.BuildServiceProvider();
        _contextFactory = _provider.GetRequiredService<IDbContextFactory<StorageDbContext>>();

        await _provider.GetRequiredService<SqliteStore>().PrepareAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();

        // SQLite keeps pooled handles on the file; without this the directory cannot be
        // removed on Windows.
        SqliteConnection.ClearAllPools();

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }

    [Fact]
    public async Task Migrations_produce_a_usable_schema()
    {
        await using var context = await _contextFactory.CreateDbContextAsync(Token);

        Assert.Empty(await context.Categories.ToListAsync(Token));
        Assert.Empty(await context.Products.ToListAsync(Token));
    }

    [Fact]
    public async Task The_database_runs_in_write_ahead_logging_mode()
    {
        await using var context = await _contextFactory.CreateDbContextAsync(Token);
        var connection = (SqliteConnection)context.Database.GetDbConnection();
        await connection.OpenAsync(Token);

        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode;";

        // Without WAL a goods-receiving session would block the counter from reading.
        var mode = (string?)await command.ExecuteScalarAsync(Token);
        Assert.Equal("wal", mode, ignoreCase: true);
    }

    [Fact]
    public async Task A_category_tree_survives_a_round_trip()
    {
        var beverages = Category.CreateRoot("Bebidas");
        var energy = beverages.CreateChild("Energéticos");

        await using (var context = await _contextFactory.CreateDbContextAsync(Token))
        {
            context.Categories.AddRange(beverages, energy);
            await context.SaveChangesAsync(Token);
        }

        await using (var context = await _contextFactory.CreateDbContextAsync(Token))
        {
            var reloaded = await context.Categories
                .SingleAsync(category => category.Id == energy.Id, Token);

            Assert.Equal(beverages.Id, reloaded.ParentId);
            Assert.Equal(1, reloaded.Depth);
            Assert.Equal(energy.Path, reloaded.Path);
        }
    }

    [Fact]
    public async Task Descendants_are_found_by_a_path_prefix()
    {
        var beverages = Category.CreateRoot("Bebidas");
        var energy = beverages.CreateChild("Energéticos");
        var sodas = beverages.CreateChild("Refrigerantes");
        var grocery = Category.CreateRoot("Mercearia");

        await using (var context = await _contextFactory.CreateDbContextAsync(Token))
        {
            context.Categories.AddRange(beverages, energy, sodas, grocery);
            await context.SaveChangesAsync(Token);
        }

        await using (var context = await _contextFactory.CreateDbContextAsync(Token))
        {
            var prefix = beverages.DescendantPathPrefix;

            var underBeverages = await context.Categories
                .Where(category => category.Path.StartsWith(prefix))
                .Select(category => category.Name)
                .ToListAsync(Token);

            Assert.Equal(3, underBeverages.Count);
            Assert.DoesNotContain("Mercearia", underBeverages);
        }
    }

    [Fact]
    public async Task Money_is_stored_as_a_whole_number_of_cents()
    {
        var product = await SaveProductAsync("Energético 473ml", "7891000000014", 8.99m);

        await using var context = await _contextFactory.CreateDbContextAsync(Token);
        var connection = (SqliteConnection)context.Database.GetDbConnection();
        await connection.OpenAsync(Token);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT SalePrice, typeof(SalePrice) FROM Products WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", product.Id);

        await using var reader = await command.ExecuteReaderAsync(Token);
        Assert.True(await reader.ReadAsync(Token));

        // The column holds 899, not 8.99: no float, no text, so SUM and ORDER BY stay exact.
        Assert.Equal(899L, reader.GetInt64(0));
        Assert.Equal("integer", reader.GetString(1), ignoreCase: true);
    }

    [Fact]
    public async Task A_barcode_is_normalised_to_fourteen_digits_in_the_column()
    {
        await SaveProductAsync("Energético 473ml", "7891000000014", 8.99m);

        await using var context = await _contextFactory.CreateDbContextAsync(Token);
        var connection = (SqliteConnection)context.Database.GetDbConnection();
        await connection.OpenAsync(Token);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Gtin FROM PackagingUnits;";

        Assert.Equal("07891000000014", (string?)await command.ExecuteScalarAsync(Token));
    }

    [Fact]
    public async Task The_same_barcode_cannot_point_at_two_products()
    {
        await SaveProductAsync("Energético 473ml", "7891000000014", 8.99m);

        // A mistyped code at registration would otherwise hijack another product at the counter.
        await Assert.ThrowsAsync<DbUpdateException>(
            async () => await SaveProductAsync("Refrigerante 350ml", "7891000000014", 4.50m));
    }

    [Fact]
    public async Task Timestamps_come_from_the_injected_clock()
    {
        var product = await SaveProductAsync("Energético 473ml", "7891000000014", 8.99m);

        await using var context = await _contextFactory.CreateDbContextAsync(Token);
        var reloaded = await context.Products
            .SingleAsync(candidate => candidate.Id == product.Id, Token);

        Assert.Equal(Now, reloaded.CreatedAt);
        Assert.Equal(Now, reloaded.UpdatedAt);
    }

    [Fact]
    public async Task A_product_round_trips_with_all_its_packagings()
    {
        var product = await SaveProductAsync(
            "Energético 473ml", "7891000000014", 8.99m, pack: "17891000000011");

        await using var context = await _contextFactory.CreateDbContextAsync(Token);
        var reloaded = await context.Products
            .Include(candidate => candidate.Packagings)
            .SingleAsync(candidate => candidate.Id == product.Id, Token);

        Assert.Equal(2, reloaded.Packagings.Count);
        Assert.Equal(899, reloaded.SalePrice.Cents);
        Assert.Equal(UnitOfMeasure.Unit, reloaded.BaseUnit);
        Assert.Equal(12, reloaded.BaseUnitsFor(Gtin.Parse("17891000000011")));
    }

    [Fact]
    public async Task Backup_writes_a_copy_that_opens_on_its_own()
    {
        await SaveProductAsync("Energético 473ml", "7891000000014", 8.99m);

        var backupPath = Path.Combine(_directory, "backup", "storage-backup.db");
        await _provider.GetRequiredService<SqliteStore>().BackupToAsync(backupPath, Token);

        Assert.True(File.Exists(backupPath));

        await using var connection = new SqliteConnection($"Data Source={backupPath};Mode=ReadOnly");
        await connection.OpenAsync(Token);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM Products;";

        Assert.Equal(1L, (long)(await command.ExecuteScalarAsync(Token))!);
    }

    private async Task<Product> SaveProductAsync(
        string name,
        string barcode,
        decimal price,
        string? pack = null)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(Token);

        var category = await context.Categories.FirstOrDefaultAsync(Token);
        if (category is null)
        {
            category = Category.CreateRoot("Bebidas");
            context.Categories.Add(category);
        }

        var product = Product.Create(
            name,
            category.Id,
            UnitOfMeasure.Unit,
            Money.FromDecimal(price),
            Gtin.Parse(barcode));

        if (pack is not null)
        {
            product.AddPackaging(Gtin.Parse(pack), "Fardo 12", conversionFactor: 12);
        }

        context.Products.Add(product);
        await context.SaveChangesAsync(Token);

        return product;
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
