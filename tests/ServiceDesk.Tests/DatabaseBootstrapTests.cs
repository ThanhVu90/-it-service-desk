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
            await db.Database.EnsureCreatedAsync(); // Simulates the pre-migration bootstrap.
            await db.Database.ExecuteSqlRawAsync("DROP INDEX IF EXISTS IX_AuditEvents_Entity_EntityId");
            db.Users.AddRange(new User { Email = "alex@example.test", Name = "Alex", Role = Roles.Employee, PasswordHash = "x" }, new User { Email = "sam@example.test", Name = "Sam", Role = Roles.Technician, PasswordHash = "x" });
            await db.SaveChangesAsync();
            var now = DateTime.UtcNow;
            db.Tickets.Add(new Ticket { Title = "Pre-migration ticket", Description = "Data that must survive", RequesterId = 1, CreatedAt = now, DueAt = now });
            db.Assets.Add(new Asset { Tag = "LT-001", Model = "Legacy asset" });
            await db.SaveChangesAsync();
            db.Assignments.Add(new Assignment { AssetId = 1, UserId = 2, AssignedAt = now });
            db.AuditEvents.Add(new AuditEvent { ActorId = 1, Entity = "Ticket", EntityId = 1, Action = "Created", At = now });
            await db.SaveChangesAsync();
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
