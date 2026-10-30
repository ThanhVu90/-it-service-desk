using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ServiceDesk.Data;
using ServiceDesk.Domain;
using ServiceDesk.Services;

namespace ServiceDesk.Tests;

public sealed class DatabaseBootstrapTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("desk-bootstrap-").FullName;
    private static DeskDb NewDb(string path) => new(new DbContextOptionsBuilder<DeskDb>().UseSqlite($"Data Source={path};Foreign Keys=True;Pooling=False").Options);

    [Fact]
    public async Task Fresh_empty_database_starts_through_controlled_migrations()
    {
        var file = Path.Combine(root, "fresh.db");
        await using var db = NewDb(file);
        var result = await DeskDbInitializer.InitializeAsync(db, NullLogger.Instance);
        Assert.Equal(DatabaseBootstrapAction.FreshMigrated, result.Action);
        Assert.Null(result.BackupPath);
        var applied = await db.Database.GetAppliedMigrationsAsync();
        Assert.Equal(applied.ToList().Count, db.Database.GetMigrations().Count());
        Assert.Contains(await IndexNames(db, "AuditEvents"), name => name == "IX_AuditEvents_Entity_EntityId");
    }

    [Fact]
    public async Task Database_with_migration_history_is_never_adopted_as_legacy_on_restart()
    {
        var file = Path.Combine(root, "managed.db");
        await using var first = NewDb(file);
        var created = await DeskDbInitializer.InitializeAsync(first, NullLogger.Instance);
        Assert.Equal(DatabaseBootstrapAction.FreshMigrated, created.Action);

        await using var restart = NewDb(file);
        var again = await DeskDbInitializer.InitializeAsync(restart, NullLogger.Instance);
        Assert.Equal(DatabaseBootstrapAction.AlreadyCurrent, again.Action);
        Assert.Null(again.BackupPath);
    }

    [Fact]
    public async Task Legacy_database_created_without_migrations_adopted_and_upgraded_without_losing_data()
    {
        var file = Path.Combine(root, "legacy.db");
        await using (var db = NewDb(file))
        {
            // Reconstruct the shape of the pre-migration release: the baseline migration's schema without any migration history.
            await db.Database.MigrateAsync(db.Database.GetMigrations().First());
            await db.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS \"__EFMigrationsLock\"");
            await db.Database.ExecuteSqlRawAsync("DROP TABLE \"__EFMigrationsHistory\"");
            // Seed with plain SQL because the live model already knows about columns this old release never had.
            await db.Database.ExecuteSqlRawAsync("INSERT INTO Users (Email, Name, Role, PasswordHash) VALUES ('alex@example.test','Alex','Employee','x'), ('sam@example.test','Sam','Technician','x')");
            await db.Database.ExecuteSqlRawAsync("INSERT INTO Tickets (Title, Description, RequesterId, Priority, Status, CreatedAt, DueAt, Resolution, Version) VALUES ('Pre-migration ticket','Data that must survive',1,1,0,'2026-01-01 00:00:00','2026-01-02 00:00:00','',1)");
            await db.Database.ExecuteSqlRawAsync("INSERT INTO Assets (Tag, Model, Version) VALUES ('LT-001','Legacy asset',1)");
            await db.Database.ExecuteSqlRawAsync("INSERT INTO Assignments (AssetId, UserId, AssignedAt) VALUES (1,2,'2026-01-01 00:00:00')");
            await db.Database.ExecuteSqlRawAsync("INSERT INTO AuditEvents (ActorId, Entity, EntityId, Action, At) VALUES (1,'Ticket',1,'Created','2026-01-01 00:00:00')");
        }

        DatabaseBootstrapResult result;
        await using (var db = NewDb(file))
        {
            result = await DeskDbInitializer.InitializeAsync(db, NullLogger.Instance);
            Assert.Equal(DatabaseBootstrapAction.LegacyAdopted, result.Action);
            Assert.True(File.Exists(result.BackupPath));
            Assert.Contains(await IndexNames(db, "AuditEvents"), name => name == "IX_AuditEvents_Entity_EntityId");
        }

        await using (var db = NewDb(file))
        {
            var service = new DeskService(db, TimeProvider.System);
            var ticket = await service.CreateTicket(new Actor(1, Roles.Employee), "Post-migration ticket", "Restore acceptance", Priority.Normal);
            Assert.Equal(2, await db.Tickets.AsNoTracking().CountAsync());
            Assert.All(await db.Tickets.AsNoTracking().ToListAsync(), x => Assert.Equal(1, x.RequesterId));
            Assert.Equal(1, await db.AuditEvents.AsNoTracking().CountAsync(x => x.Entity == "Ticket" && x.EntityId == 1));
            Assert.Equal(1, await db.AuditEvents.AsNoTracking().CountAsync(x => x.Entity == "Ticket" && x.EntityId == ticket.Id));
        }
        Assert.True(File.Exists(result.BackupPath));
    }

    [Fact]
    public async Task Unrecognized_schema_is_refused_and_safety_backup_is_saved()
    {
        var file = Path.Combine(root, "foreign.db");
        await using (var db = NewDb(file))
        {
            await db.Database.EnsureCreatedAsync();
            db.Users.Add(new User { Email = "alex@example.test", Name = "Alex", Role = Roles.Employee, PasswordHash = "x" });
            await db.SaveChangesAsync();
            db.Tickets.Add(new Ticket { Title = "Original content", Description = "Keep", RequesterId = 1, CreatedAt = DateTime.UtcNow, DueAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE Tickets RENAME COLUMN Version TO Epoch");
        }

        var backups = Path.Combine(root, "backups");
        await using var attempt = NewDb(file);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => DeskDbInitializer.InitializeAsync(attempt, NullLogger.Instance, backups));
        Assert.Contains(backups, error.Message);
        var saved = Directory.EnumerateFiles(backups, "*.db").ToList();
        Assert.NotEmpty(saved);
        await using var copy = NewDb(saved[0]);
        Assert.Equal(1L, await InlineCountAsync(copy, "Tickets"));
        Assert.Equal(1L, await InlineCountAsync(copy, "Users"));
    }

    private static async Task<long> InlineCountAsync(DeskDb db, string table)
    {
        var connection = (Microsoft.Data.Sqlite.SqliteConnection)db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM \"{table}\"";
        var scalar = await command.ExecuteScalarAsync();
        return Convert.ToInt64(scalar);
    }

    [Fact]
    public async Task Corrupt_database_file_fails_startup_before_reporting_ready()
    {
        var file = Path.Combine(root, "corrupt.db");
        await File.WriteAllTextAsync(file, new string('G', 8192));
        await using var db = NewDb(file);
        _ = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => DeskDbInitializer.InitializeAsync(db, NullLogger.Instance));
    }

    [Fact]
    public async Task Backup_restores_roundtrip_preserves_live_database_writes()
    {
        var file = Path.Combine(root, "live.db");
        var snapshot = Path.Combine(root, "snapshot.db");
        var restored = Path.Combine(root, "restored.db");
        await using (var db = NewDb(file))
        {
            await db.Database.EnsureCreatedAsync();
            db.Users.Add(new User { Email = "alex@example.test", Name = "Alex", Role = Roles.Employee, PasswordHash = "x" });
            await db.SaveChangesAsync();
        }
        DbMaintenance.Backup(file, snapshot);
        await using (var db = NewDb(file))
        {
            db.Tickets.Add(new Ticket { Title = "Written after backup", Description = "Not in the snapshot", RequesterId = 1, CreatedAt = DateTime.UtcNow, DueAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        DbMaintenance.Restore(snapshot, restored);
        await using (var live = NewDb(file))
            Assert.Equal(1, await live.Tickets.AsNoTracking().CountAsync());
        await using (var copy = NewDb(restored))
        {
            Assert.Equal(0, await copy.Tickets.AsNoTracking().CountAsync());
            Assert.Equal(1, await copy.Users.AsNoTracking().CountAsync());
            var service = new DeskService(copy, TimeProvider.System);
            await service.CreateTicket(new Actor(1, Roles.Employee), "Ticket on restored copy", "Acceptance requires new writes", Priority.Normal);
            Assert.Equal(1, await copy.Tickets.AsNoTracking().CountAsync());
        }
    }

    [Fact]
    public void Backup_never_overwrites_an_existing_destination()
    {
        var source = Path.Combine(root, "never.db");
        var target = Path.Combine(root, "existing.db");
        File.WriteAllText(source, new string('G', 2048));
        File.WriteAllText(target, "guard content");
        Assert.ThrowsAny<InvalidOperationException>(() => DbMaintenance.Backup(source, target));
        Assert.Equal("guard content", File.ReadAllText(target));
    }

    [Fact]
    public async Task Forced_replace_writes_a_verified_copy_in_place_of_the_old_destination()
    {
        var live = Path.Combine(root, "live.db");
        var stale = Path.Combine(root, "stale.db");
        await using (var db = NewDb(live))
        {
            await db.Database.EnsureCreatedAsync();
            db.Users.Add(new User { Email = "alex@example.test", Name = "Alex", Role = Roles.Employee, PasswordHash = "x" });
            await db.SaveChangesAsync();
        }
        File.WriteAllText(stale, "stale content that was never a valid database");
        var code = DbMaintenance.RunCli(["db-restore", "--source", live, "--out", stale, "--force"]);
        Assert.Equal(0, code);
        await using var verify = NewDb(stale);
        Assert.Equal(1, await verify.Users.AsNoTracking().CountAsync());
    }

    [Fact]
    public void Forced_replace_never_touches_the_destination_when_the_copy_fails()
    {
        var source = Path.Combine(root, "broken.db");
        var target = Path.Combine(root, "guard.db");
        File.WriteAllText(source, new string('G', 8192)); // unreadable as a SQLite database
        File.WriteAllText(target, "guard content");
        var code = DbMaintenance.RunCli(["db-backup", "--source", source, "--out", target, "--force"]);
        Assert.Equal(3, code);
        Assert.Equal("guard content", File.ReadAllText(target));
        Assert.Empty(Directory.EnumerateFiles(root, "*.stage-*")); // the failed staging copy is cleaned up
    }

    [Fact]
    public async Task RunCli_refuses_a_destination_that_resolves_to_the_source()
    {
        var file = Path.Combine(root, "self.db");
        await using (var db = NewDb(file))
        {
            await db.Database.EnsureCreatedAsync();
            db.Users.Add(new User { Email = "alex@example.test", Name = "Alex", Role = Roles.Employee, PasswordHash = "x" });
            await db.SaveChangesAsync();
        }
        var before = File.ReadAllText(file);
        Assert.Equal(3, DbMaintenance.RunCli(["db-backup", "--source", file, "--out", file]));
        Assert.Equal(3, DbMaintenance.RunCli(["db-backup", "--source", file, "--out", file, "--force"]));
        Assert.Equal(3, DbMaintenance.RunCli(["db-restore", "--source", file, "--out", file, "--force"]));
        Assert.Equal(before, File.ReadAllText(file)); // the source survives every invocation, byte for byte
    }

    // A pre-migration database is reconstructed from the baseline migration's own DDL, so only the
    // semantic detail under test separates it from a schema the initializer must still adopt.
    private static async Task BuildLegacyDatabase(string file, string? search = null, string? replace = null)
    {
        await using var scratch = new DeskDb(new DbContextOptionsBuilder<DeskDb>().UseSqlite("Data Source=:memory:").Options);
        await scratch.Database.OpenConnectionAsync();
        await scratch.Database.MigrateAsync(scratch.Database.GetMigrations().First());
        var source = (SqliteConnection)scratch.Database.GetDbConnection();
        var statements = new List<string>();
        await using (var read = source.CreateCommand())
        {
            // Entity DDL only: engine-shadowed tables (sqlite_sequence) and the EF-internal
            // history tables are part of a fresh migration, not of a pre-migration release database.
            read.CommandText = "SELECT name, sql FROM sqlite_master WHERE sql IS NOT NULL ORDER BY rowid";
            var reader = await read.ExecuteReaderAsync();
            await using (reader)
                while (await reader.ReadAsync())
                {
                    var name = reader.GetString(0);
                    if (name.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("__", StringComparison.Ordinal)) continue;
                    statements.Add(reader.GetString(1));
                }
        }
        await using var connection = new SqliteConnection($"Data Source={file};Pooling=False");
        connection.Open();
        var mutated = 0;
        foreach (var statement in statements)
        {
            var executed = statement;
            if (search != null)
            {
                // Compare and mutate on whitespace-normalized SQL so the mutation matches whatever line
                // breaks the migration generator used; flattening never changes executed semantics.
                var flat = System.Text.RegularExpressions.Regex.Replace(statement, @"\s+", " ");
                if (flat.Contains(search))
                {
                    mutated++;
                    executed = flat.Replace(search, replace!);
                }
            }
            await using var command = connection.CreateCommand();
            command.CommandText = executed;
            await command.ExecuteNonQueryAsync();
        }
        if (search != null) Assert.Equal(1, mutated); // the mutation must land on exactly one statement, or the DDL drifted
    }

    [Fact]
    public async Task Rebuilt_baseline_without_any_semantic_change_is_still_adopted()
    {
        var file = Path.Combine(root, "faithful-rebuild.db");
        await BuildLegacyDatabase(file);
        await using var db = NewDb(file);
        var result = await DeskDbInitializer.InitializeAsync(db, NullLogger.Instance);
        Assert.Equal(DatabaseBootstrapAction.LegacyAdopted, result.Action);
    }

    [Theory]
    [InlineData("a UNIQUE constraint", "CREATE UNIQUE INDEX \"IX_Users_Email\"", "CREATE INDEX \"IX_Users_Email\"")]
    [InlineData("NOT NULL", "\"Role\" TEXT NOT NULL", "\"Role\" TEXT")]
    [InlineData("a column type", "\"Role\" TEXT NOT NULL", "\"Role\" INTEGER NOT NULL")]
    [InlineData("a foreign key", ", CONSTRAINT \"FK_Assignments_Users_UserId\" FOREIGN KEY (\"UserId\") REFERENCES \"Users\" (\"Id\") ON DELETE RESTRICT )", " )")]
    [InlineData("a partial index condition", " WHERE \"ReturnedAt\" IS NULL", "")]
    public async Task Schema_that_lost_semantics_never_matches_the_baseline_fingerprint(string loss, string search, string replace)
    {
        var file = Path.Combine(root, $"lost-{loss.Replace(' ', '-')}.db");
        await BuildLegacyDatabase(file, search, replace);
        await using var db = NewDb(file);
        var error = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => DeskDbInitializer.InitializeAsync(db, NullLogger.Instance));
        Assert.Contains("Schema does not match baseline", error.Message);
        // The same database with the mutation applied is refused, while the faithful rebuild adopted:
        // this test pair isolates exactly that semantic detail.
    }

    [Fact]
    public async Task Failure_between_the_history_writes_leaves_no_half_created_history_table()
    {
        var file = Path.Combine(root, "interrupted-adoption.db");
        await BuildLegacyDatabase(file);
        DeskDbInitializer.BetweenHistoryWrites = () => throw new InvalidOperationException("injected failure between the two history writes");
        try
        {
            await using var db = NewDb(file);
            var error = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => DeskDbInitializer.InitializeAsync(db, NullLogger.Instance));
            Assert.Contains("injected failure", error.Message);
        }
        finally
        {
            DeskDbInitializer.BetweenHistoryWrites = null;
        }
        await using var verify = NewDb(file);
        {
            var connection = (SqliteConnection)verify.Database.GetDbConnection();
            if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name = '__EFMigrationsHistory'";
            Assert.Equal(0L, await command.ExecuteScalarAsync()); // rolled back: no history table without its baseline row
        }
        await using var retry = NewDb(file);
        var adopted = await DeskDbInitializer.InitializeAsync(retry, NullLogger.Instance);
        Assert.Equal(DatabaseBootstrapAction.LegacyAdopted, adopted.Action); // nothing half-written blocks the retry
    }

    private static async Task<List<string>> IndexNames(DeskDb db, string table)
    {
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT name FROM sqlite_master WHERE type='index' AND tbl_name='{table}' AND name NOT LIKE 'sqlite_autoindex_%'";
        var names = new List<string>();
        var reader = await command.ExecuteReaderAsync();
        await using (reader)
            while (await reader.ReadAsync()) names.Add(reader.GetString(0));
        return names;
    }

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch (IOException) { }
    }
}
