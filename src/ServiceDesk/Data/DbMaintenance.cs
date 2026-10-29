using Microsoft.Data.Sqlite;

namespace ServiceDesk.Data;

public static class DbMaintenance
{
    public static void Backup(string sourcePath, string destinationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        if (!File.Exists(sourcePath))
            throw new InvalidOperationException($"Source database '{sourcePath}' was not found.");
        if (File.Exists(destinationPath))
            throw new InvalidOperationException($"Destination '{destinationPath}' already exists. Choose a new file name so snapshots are never silently replaced.");

        using var source = new SqliteConnection($"Data Source={sourcePath};Mode=ReadOnly;Pooling=False");
        using var destination = new SqliteConnection($"Data Source={destinationPath};Pooling=False");
        source.Open();
        destination.Open();
        source.BackupDatabase(destination); // sqlite3_backup_* full copy: a consistent snapshot including pending WAL pages.
        VerifyIntegrity(destination, destinationPath, deleteOnFailure: true);
    }

    public static void Restore(string sourcePath, string destinationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        if (!File.Exists(sourcePath))
            throw new InvalidOperationException($"Snapshot '{sourcePath}' was not found.");
        if (File.Exists(destinationPath))
            throw new InvalidOperationException($"Destination '{destinationPath}' already exists. Choose a new file name so live data is never silently replaced.");

        File.Copy(sourcePath, destinationPath, overwrite: false);
        using var destination = new SqliteConnection($"Data Source={destinationPath};Pooling=False");
        destination.Open();
        VerifyIntegrity(destination, destinationPath, deleteOnFailure: true);
    }

    public static int RunCli(string[] args)
    {
        var command = args[0].ToLowerInvariant();
        if (command is not ("db-backup" or "db-restore"))
        {
            Console.Error.WriteLine($"Unknown maintenance command '{args[0]}'.");
            PrintUsage();
            return 2;
        }
        string? source = null, output = null;
        var force = false;
        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--source":
                    source = (++i < args.Length) ? args[i] : null;
                    break;
                case "--out":
                    output = (++i < args.Length) ? args[i] : null;
                    break;
                case "--force":
                    force = true;
                    break;
                default:
                    Console.Error.WriteLine($"Unknown option '{args[i]}'.");
                    PrintUsage();
                    return 2;
            }
        }
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(output))
        {
            PrintUsage();
            return 2;
        }
        try
        {
            if (command == "db-backup")
            {
                if (force && File.Exists(output)) File.Delete(output);
                Backup(source, output);
                Console.WriteLine($"Backup complete: '{source}' -> '{output}' ({new FileInfo(output).Length} bytes).");
                return 0;
            }
            if (force && File.Exists(output)) File.Delete(output);
            Restore(source, output);
            Console.WriteLine($"Restore complete: '{source}' -> '{output}' (integrity check passed).");
            return 0;
        }
        catch (Exception failure)
        {
            Console.Error.WriteLine($"{command} failed: {failure.Message}");
            return 3;
        }

        static void PrintUsage() => Console.Error.WriteLine("Usage: ServiceDesk db-backup|db-restore --source <database.db> --out <target.db> [--force]");
    }

    private static void VerifyIntegrity(SqliteConnection connection, string databasePath, bool deleteOnFailure)
    {
        using var check = connection.CreateCommand();
        check.CommandText = "PRAGMA integrity_check";
        var result = check.ExecuteScalar() as string;
        if (result == "ok") return;
        if (deleteOnFailure) try { connection.Close(); File.Delete(databasePath); } catch (IOException) { }
        throw new InvalidOperationException($"The copy at '{databasePath}' failed the SQLite integrity check ({result ?? "unknown"}).");
    }
}
