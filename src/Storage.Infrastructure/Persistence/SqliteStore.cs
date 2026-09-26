using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Storage.Infrastructure.Persistence;

/// <summary>
/// Owns the database file: prepares it at startup and copies it for backup.
/// </summary>
public sealed class SqliteStore(
    IDbContextFactory<StorageDbContext> contextFactory,
    ILogger<SqliteStore> logger)
{
    public async Task PrepareAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var connection = (SqliteConnection)context.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken);

        // Write-ahead logging is a property of the file and survives restarts, but setting
        // it on every boot also covers a database restored from a backup. It is what lets
        // the counter keep reading while a goods-receiving session writes.
        await ExecuteAsync(connection, "PRAGMA journal_mode=WAL;", cancellationToken);

        await context.Database.MigrateAsync(cancellationToken);

        logger.LogInformation("Database ready at {DataSource}", connection.DataSource);
    }

    /// <summary>
    /// Hot backup of the live database.
    /// </summary>
    /// <remarks>
    /// VACUUM INTO is the only safe way to copy a SQLite database that is open: copying
    /// the file by hand while a write is in flight yields a corrupt backup, and with WAL
    /// it would also miss whatever still sits in the -wal sidecar.
    /// </remarks>
    public async Task BackupToAsync(string destinationPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        var directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = (SqliteConnection)context.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "VACUUM INTO $destination;";
        command.Parameters.AddWithValue("$destination", destinationPath);
        await command.ExecuteNonQueryAsync(cancellationToken);

        logger.LogInformation("Backup written to {Destination}", destinationPath);
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
