using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CoreX.Hub.Infrastructure.Persistence;

/// <summary>
/// ADR-0013: every SQLite connection runs in WAL mode with a busy timeout and with
/// foreign keys enforced. SQLite disables foreign keys per connection by default, so
/// this is not optional.
/// </summary>
public sealed class SqlitePragmaInterceptor : DbConnectionInterceptor
{
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        ApplyPragmas(connection);
        base.ConnectionOpened(connection, eventData);
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        ApplyPragmas(connection);
        await base.ConnectionOpenedAsync(connection, eventData, cancellationToken);
    }

    private static void ApplyPragmas(DbConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "PRAGMA journal_mode=WAL;" +
            "PRAGMA busy_timeout=5000;" +
            "PRAGMA foreign_keys=ON;" +
            "PRAGMA synchronous=NORMAL;";
        command.ExecuteNonQuery();
    }
}
