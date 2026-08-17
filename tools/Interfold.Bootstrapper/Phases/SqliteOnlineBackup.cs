using Microsoft.Data.Sqlite;

namespace Interfold.Bootstrapper.Phases;

/// <summary>Host-side SQLite online backup via <see cref="SqliteConnection.BackupDatabase"/>.</summary>
internal static class SqliteOnlineBackup
{
    public static async Task BackupAsync(string sourceDbPath, string destinationDbPath, CancellationToken ct)
    {
        var destDir = Path.GetDirectoryName(destinationDbPath);
        if (!string.IsNullOrEmpty(destDir))
            Directory.CreateDirectory(destDir);

        if (File.Exists(destinationDbPath))
            File.Delete(destinationDbPath);

        await using var source = new SqliteConnection($"Data Source={sourceDbPath}");
        await source.OpenAsync(ct).ConfigureAwait(false);
        await using var destination = new SqliteConnection($"Data Source={destinationDbPath}");
        await destination.OpenAsync(ct).ConfigureAwait(false);
        source.BackupDatabase(destination);
    }
}
