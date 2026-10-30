using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ServiceDesk.Data;

public enum DatabaseBootstrapAction { FreshMigrated, LegacyAdopted, Migrated, AlreadyCurrent }

public sealed record DatabaseBootstrapResult(DatabaseBootstrapAction Action, string? BackupPath = null);

public static class DeskDbInitializer
{
    internal static readonly string[] KnownEntityTables = ["Users", "Tickets", "Assets", "Assignments", "AuditEvents"];
    private const string HistoryTable = "__EFMigrationsHistory";

    public static async Task<DatabaseBootstrapResult> InitializeAsync(DeskDb db, ILogger logger, string? backupDirectory = null)
    {
        if (!db.Database.IsSqlite())
            throw new InvalidOperationException("The controlled bootstrap routine only manages the SQLite provider; PostgreSQL remains experimental.");

        var migrations = db.Database.GetMigrations().ToArray();
        if (migrations.Length == 0)
            throw new InvalidOperationException("No migrations were found in this build; refusing to touch the database.");

        var connection = (SqliteConnection)db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync();

        DbLayout layout;
        try
        {
            layout = await ReadLayoutAsync(connection);
        }
        catch (SqliteException unreadable)
        {
            throw new InvalidOperationException($"The database file at '{connection.DataSource}' cannot be read as a SQLite database ({unreadable.Message}). Nothing was modified; restore it from a snapshot or point the application at a fresh database.", unreadable);
        }

        if (layout.EntityTables.Count == 0)
        {
            await db.Database.MigrateAsync();
            logger.LogInformation("Fresh SQLite database created with {Count} migration(s) from {Baseline} to {Tip}.", migrations.Length, migrations[0], migrations[^1]);
            return new DatabaseBootstrapResult(DatabaseBootstrapAction.FreshMigrated);
        }

        if (layout.HasHistoryTable)
        {
            var pending = migrations.Where(x => !layout.AppliedMigrations.Contains(x)).ToArray();
            if (pending.Length == 0)
            {
                logger.LogInformation("SQLite database schema is already at the current migration level ({Tip}).", migrations[^1]);
                return new DatabaseBootstrapResult(DatabaseBootstrapAction.AlreadyCurrent);
            }
            var snapshot = await SnapshotAsync(connection, backupDirectory, "upgrade", logger);
            try
            {
                await db.Database.MigrateAsync();
            }
            catch (Exception failure)
            {
                throw new InvalidOperationException($"Schema upgrade failed ({failure.Message}). The data snapshot remains at '{snapshot}'.", failure);
            }
            logger.LogInformation("Upgraded SQLite database by applying {Count} migration(s): {Pending}.", pending.Length, string.Join(", ", pending));
            return new DatabaseBootstrapResult(DatabaseBootstrapAction.Migrated, snapshot);
        }

        // Entity tables exist but there is no migration history: a database from a pre-migration release.
        var baselineMigration = migrations[0];
        var snapshotBefore = await SnapshotAsync(connection, backupDirectory, "adopt", logger);
        if (snapshotBefore is null)
            throw new InvalidOperationException("A database created before migrations cannot be adopted without a file snapshot; point the application at a file-backed database. Nothing was modified.");

        var baseline = await DescribeBaselineAsync(baselineMigration);
        var current = await ReadLayoutAsync(connection);
        if (current.Fingerprint != baseline.Schema)
            throw new InvalidOperationException($"Schema does not match baseline migration '{baselineMigration}', so automatic adoption refuses to continue. The data snapshot remains at '{snapshotBefore}'. Restore or repair that schema manually, then start again.");

        await MarkBaselineAppliedAsync(connection, baselineMigration, baseline.ProductVersion);
        if (migrations.Length > 1)
        {
            try
            {
                await db.Database.MigrateAsync();
            }
            catch (Exception failure)
            {
                throw new InvalidOperationException($"Upgrade after adoption failed ({failure.Message}). The snapshot remains at '{snapshotBefore}'.", failure);
            }
        }
        logger.LogInformation("Adopted pre-migration SQLite database against baseline '{Baseline}' and upgraded it to '{Tip}'. Snapshot: {Snapshot}.", baselineMigration, migrations[^1], snapshotBefore);
        return new DatabaseBootstrapResult(DatabaseBootstrapAction.LegacyAdopted, snapshotBefore);
    }

    private static async Task<(string Schema, string ProductVersion)> DescribeBaselineAsync(string baselineMigration)
    {
        await using var scratch = new DeskDb(new DbContextOptionsBuilder<DeskDb>().UseSqlite("Data Source=:memory:").Options);
        await scratch.Database.OpenConnectionAsync(); // Keep one in-memory database alive for the whole probe.
        await scratch.Database.MigrateAsync(baselineMigration);
        var connection = (SqliteConnection)scratch.Database.GetDbConnection();
        var schema = await DescribeSchemaAsync(connection);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT \"ProductVersion\" FROM \"{HistoryTable}\" WHERE \"MigrationId\" = @id";
        command.Parameters.AddWithValue("@id", baselineMigration);
        var productVersion = await command.ExecuteScalarAsync() as string ?? throw new InvalidOperationException("The baseline migration left no history row.");
        return (schema, productVersion);
    }

    private static async Task<DbLayout> ReadLayoutAsync(SqliteConnection connection)
    {
        var tables = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        var indexSql = new SortedDictionary<string, string>(StringComparer.Ordinal);
        await using (var master = connection.CreateCommand())
        {
            master.CommandText = "SELECT name, type, tbl_name, sql FROM sqlite_master";
            var reader = await master.ExecuteReaderAsync();
            await using (reader)
                while (await reader.ReadAsync())
                {
                    var (name, kind, table) = (reader.GetString(0), reader.GetString(1), reader.GetString(2));
                    if (name.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase)) continue; // engine internals never matter here.
                    switch (kind)
                    {
                        case "table":
                            tables[name] = []; // EF-internal tables stay listed for history detection and are excluded from the fingerprint.
                            break;
                        case "index":
                            if (name.StartsWith("__", StringComparison.Ordinal)) continue;
                            if (!reader.IsDBNull(3)) indexSql[name] = reader.GetString(3);
                            break;
                    }
                }
        }
        foreach (var (table, columns) in tables)
        {
            if (table.StartsWith("__", StringComparison.Ordinal)) continue;
            await using var info = connection.CreateCommand();
            info.CommandText = $"PRAGMA table_info({Quote(table)})";
            var reader = await info.ExecuteReaderAsync();
            await using (reader)
                while (await reader.ReadAsync())
                {
                    // The full column definition decides the fingerprint: declared type, NULL constraint,
                    // primary-key position and the default clause, not just the column name.
                    var fallback = reader.IsDBNull(4) ? "no-default" : Convert.ToString(reader.GetValue(4), CultureInfo.InvariantCulture) ?? "no-default";
                    columns.Add($"{reader.GetString(1)}/{reader.GetString(2)}/notnull{reader.GetInt64(3)}/pk{reader.GetInt64(5)}/default{fallback}");
                }
        }
        var knownEntities = tables.Keys.Where(KnownEntityTables.Contains).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hasHistory = tables.ContainsKey(HistoryTable);
        var applied = hasHistory ? await ReadHistoryAsync(connection) : [];

        var builder = new System.Text.StringBuilder();
        foreach (var (table, columns) in tables)
        {
            if (table.StartsWith("__", StringComparison.Ordinal)) continue;
            builder.Append('T').Append(table).Append('{');
            foreach (var column in columns) builder.Append(column).Append(';');
            await AppendForeignKeysAsync(connection, table, builder);
            await AppendIndexesAsync(connection, table, builder, indexSql);
            builder.Append('}');
        }
        return new DbLayout(knownEntities, hasHistory, applied, builder.ToString());
    }

    // Foreign keys decide the fingerprint too: the referenced table, source column, target column and delete/update actions.
    private static async Task AppendForeignKeysAsync(SqliteConnection connection, string table, System.Text.StringBuilder builder)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA foreign_key_list({Quote(table)})";
        var reader = await command.ExecuteReaderAsync();
        await using (reader)
        {
            var rules = new List<(long Id, long Seq, string Rule)>();
            while (await reader.ReadAsync())
            {
                var target = reader.IsDBNull(4) ? "no-target" : reader.GetString(4);
                rules.Add((reader.GetInt64(0), reader.GetInt64(1), $"{reader.GetString(3)}->{target}/{reader.GetString(5)}/{reader.GetString(6)}"));
            }
            foreach (var (_, _, rule) in rules.OrderBy(x => x.Id).ThenBy(x => x.Seq))
                builder.Append('f').Append(rule).Append(';');
        }
    }

    // Indexes decide the fingerprint beyond their names: uniqueness (explicit or enforced by a UNIQUE/PK constraint),
    // their exact key columns, and for partial indexes the stored WHERE condition.
    private static async Task AppendIndexesAsync(SqliteConnection connection, string table, System.Text.StringBuilder builder, SortedDictionary<string, string> indexSql)
    {
        await using var list = connection.CreateCommand();
        list.CommandText = $"PRAGMA index_list({Quote(table)})";
        var indexes = new SortedDictionary<string, (bool Unique, string Origin, bool Partial)>(StringComparer.Ordinal);
        var names = await list.ExecuteReaderAsync();
        await using (names)
            while (await names.ReadAsync())
                indexes[names.GetString(1)] = (names.GetInt64(2) != 0, names.GetString(3), names.GetInt64(4) != 0);
        foreach (var (name, (unique, origin, partial)) in indexes)
        {
            await using var info = connection.CreateCommand();
            info.CommandText = $"PRAGMA index_info({Quote(name)})";
            var keyed = new SortedDictionary<long, string>();
            var columns = await info.ExecuteReaderAsync();
            await using (columns)
                while (await columns.ReadAsync())
                    keyed[columns.GetInt64(0)] = columns.IsDBNull(2) ? "#expression" : columns.GetString(2);
            builder.Append('x').Append(name).Append('|').Append(unique ? "unique" : "non-unique").Append(origin).Append('/').Append(string.Join(',', keyed.Values));
            if (partial && indexSql.TryGetValue(name, out var sql))
                builder.Append("|where(").Append(Regex.Replace(sql, @"\s+", " ").Trim()).Append(')');
            builder.Append(';');
        }
    }

    private static async Task<string[]> ReadHistoryAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT \"MigrationId\" FROM \"{HistoryTable}\"";
        var reader = await command.ExecuteReaderAsync();
        await using (reader)
        {
            var ids = new List<string>();
            while (await reader.ReadAsync()) ids.Add(reader.GetString(0));
            return [.. ids];
        }
    }

    // Test seam only: lets tests inject a failure between the two history writes to prove they fail together.
    internal static Action? BetweenHistoryWrites { get; set; }

    private static async Task MarkBaselineAppliedAsync(SqliteConnection connection, string baselineMigration, string productVersion)
    {
        // The history table and the baseline row must commit together or not at all. A failure in between
        // would leave an "adopted" database with an empty history table, which the next start would treat
        // as migration-managed and fail instead of re-adopting it.
        await using var transaction = connection.BeginTransaction();
        await using (var create = connection.CreateCommand())
        {
            create.Transaction = transaction;
            create.CommandText = $"CREATE TABLE \"{HistoryTable}\" (\"MigrationId\" TEXT NOT NULL CONSTRAINT \"PK___EFMigrationsHistory\" PRIMARY KEY, \"ProductVersion\" TEXT NOT NULL)";
            await create.ExecuteNonQueryAsync();
            BetweenHistoryWrites?.Invoke();
        }
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = $"INSERT INTO \"{HistoryTable}\" (\"MigrationId\", \"ProductVersion\") VALUES (@id, @version)";
            insert.Parameters.AddWithValue("@id", baselineMigration);
            insert.Parameters.AddWithValue("@version", productVersion);
            await insert.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
    }

    private static async Task<string> DescribeSchemaAsync(SqliteConnection connection) => (await ReadLayoutAsync(connection)).AsFingerprint();

    private static async Task<string?> SnapshotAsync(SqliteConnection source, string? backupDirectory, string kind, ILogger logger)
    {
        var databaseFile = source.DataSource;
        if (string.IsNullOrWhiteSpace(databaseFile) || databaseFile == ":memory:") return null;
        var rooted = Path.IsPathRooted(databaseFile) ? databaseFile : Path.GetFullPath(databaseFile, Environment.CurrentDirectory);
        backupDirectory ??= Path.Combine(Path.GetDirectoryName(rooted) ?? ".", "backups");
        Directory.CreateDirectory(backupDirectory);
        var snapshotFile = Path.Combine(backupDirectory, $"desk-{kind}-{DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}-{Guid.NewGuid():N}.db");
        DbMaintenance.Backup(rooted, snapshotFile);
        logger.LogInformation("SQLite snapshot ({Kind}) saved to {Snapshot}.", kind, snapshotFile);
        return snapshotFile;
    }

    private static string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";

    internal readonly record struct DbLayout(HashSet<string> EntityTables, bool HasHistoryTable, string[] AppliedMigrations, string Fingerprint)
    {
        internal string AsFingerprint() => Fingerprint;
    }
}
