using Interfold.Bootstrapper.Phases;
using Interfold.Infrastructure.Sqlite;
using Interfold.Shared.Contracts.Configuration;
using Interfold.Shared.Contracts.Secrets;
using Microsoft.Extensions.Logging.Abstractions;

namespace Interfold.Bootstrapper.UnitTests;

public sealed class SqliteOnlineBackupTests
{
    [Test]
    public async Task OnlineBackupCopiesSecretsRow()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"interfold-sqlite-bak-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var source = Path.Combine(dir, "source.db");
        var dest = Path.Combine(dir, "backup.db");

        try
        {
            await SqliteMigrationService.MigrateAsync($"Data Source={source}", NullLogger.Instance, CancellationToken.None);
            var store = new SqliteSecretsStore(new SqliteConnectionFactory($"Data Source={source}"));
            await store.UpsertAsync(SecretsStoreKeys.EncryptionPepper, "pepper-value");

            await SqliteOnlineBackup.BackupAsync(source, dest, CancellationToken.None);

            var restored = new SqliteSecretsStore(new SqliteConnectionFactory($"Data Source={dest}"));
            await Assert.That(await restored.GetAsync(SecretsStoreKeys.EncryptionPepper)).IsEqualTo("pepper-value");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Test]
    public async Task BuildArchiveFileNameForSqlite()
    {
        await Assert.That(BackupPhase.BuildArchiveFileName(BackupDatabaseComponent.Sqlite, "20260816-120000"))
            .IsEqualTo("20260816-120000.db");
        await Assert.That(BackupPhase.ResolveSqliteDbPath("/srv/deploy"))
            .IsEqualTo(Path.Combine(PublishPhase.ResolveSqliteDataHostDir("/srv/deploy"), ContainerMountPaths.InterfoldSqliteDbFileName));
    }
}
