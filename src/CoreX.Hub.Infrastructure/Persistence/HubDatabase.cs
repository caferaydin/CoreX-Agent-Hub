using CoreX.Hub.Application.Abstractions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CoreX.Hub.Infrastructure.Persistence;

/// <summary>
/// Composition helper for the local-first SQLite database (ADR-0001, ADR-0013).
/// Deliberately plain: no DI container is involved in Phase 1.
/// </summary>
public static class HubDatabase
{
    public const string DatabaseFileName = "hub.db";

    public static DbContextOptions<HubDbContext> BuildOptions(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Default,
            Pooling = false,
        }.ToString();

        return new DbContextOptionsBuilder<HubDbContext>()
            .UseSqlite(connectionString)
            .AddInterceptors(new SqlitePragmaInterceptor())
            .Options;
    }

    public static HubDbContext CreateContext(string databasePath, OrganizationScope scope) =>
        new(BuildOptions(databasePath), scope);

    /// <summary>Applies all pending migrations. Never drops or recreates data.</summary>
    public static async Task MigrateAsync(HubDbContext db, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        await db.Database.MigrateAsync(ct);
    }

    public static async Task<string> ReadJournalModeAsync(HubDbContext db, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        await db.Database.OpenConnectionAsync(ct);
        try
        {
            var connection = db.Database.GetDbConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode;";
            var result = await command.ExecuteScalarAsync(ct);
            return result?.ToString() ?? "unknown";
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }
}
