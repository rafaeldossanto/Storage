using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Storage.Infrastructure.Persistence;

/// <summary>
/// Applies the per-connection SQLite pragmas every time a connection is opened.
/// </summary>
/// <remarks>
/// journal_mode is stored in the database file and only needs to be set once, but
/// synchronous and busy_timeout are per connection: setting them at startup alone would
/// leave every pooled connection opened afterwards on the defaults.
/// </remarks>
public sealed class SqlitePragmaInterceptor(int busyTimeoutMilliseconds = 5000) : DbConnectionInterceptor
{
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        Apply(connection);
        base.ConnectionOpened(connection, eventData);
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        await ApplyAsync(connection, cancellationToken);
        await base.ConnectionOpenedAsync(connection, eventData, cancellationToken);
    }

    private void Apply(DbConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = PragmaSql;
        command.ExecuteNonQuery();
    }

    private async Task ApplyAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = PragmaSql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private string PragmaSql =>
        $"PRAGMA synchronous=NORMAL; PRAGMA busy_timeout={busyTimeoutMilliseconds}; PRAGMA foreign_keys=ON;";
}
