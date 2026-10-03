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

        // Default pooling would keep this process's handle open after the method
        // returns. update-images then recreates the API against the same file and
        // the container open fails with SQLITE_IOERR.
        try
        {
            await using var source = new SqliteConnection(Unpooled(sourceDbPath));
            await source.OpenAsync(ct).ConfigureAwait(false);
            await using var destination = new SqliteConnection(Unpooled(destinationDbPath));
            await destination.OpenAsync(ct).ConfigureAwait(false);
            source.BackupDatabase(destination);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    private static string Unpooled(string path) =>
        new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
}
