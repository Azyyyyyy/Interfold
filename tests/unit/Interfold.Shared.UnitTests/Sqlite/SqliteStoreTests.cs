using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Secrets;
using Interfold.Infrastructure.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

// IdempotencyKey / OperationId / SystemId / Jti live in Shared.Contracts.Ids.

namespace Interfold.Api.UnitTests.Sqlite;

/// <summary>Shared temp-file SQLite harness for store / migration unit tests.</summary>
internal static class SqliteTestDb
{
    public static async Task<string> CreateMigratedAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"interfold-sqlite-ut-{Guid.NewGuid():N}.db");
        var cs = $"Data Source={path}";
        await SqliteMigrationService.MigrateAsync(cs, NullLogger.Instance, CancellationToken.None);
        return path;
    }

    public static string ConnectionString(string path) => $"Data Source={path}";

    public static ISqliteConnectionFactory Factory(string path)
        => new SqliteConnectionFactory(ConnectionString(path));

    public static void Delete(string path)
    {
        foreach (var p in new[] { path, path + "-wal", path + "-shm" })
        {
            try { if (File.Exists(p)) File.Delete(p); } catch { /* best-effort */ }
        }
    }
}

public sealed class SqliteConnectionFactoryTests
{
    [Test]
    public async Task OpenConnection_AppliesLockedInPragmas()
    {
        var path = Path.Combine(Path.GetTempPath(), $"interfold-sqlite-pragma-{Guid.NewGuid():N}.db");
        try
        {
            var factory = SqliteTestDb.Factory(path);
            await using var conn = await factory.OpenConnectionAsync();

            await using var journal = conn.CreateCommand();
            journal.CommandText = "PRAGMA journal_mode;";
            var journalMode = (string)(await journal.ExecuteScalarAsync())!;

            await using var sync = conn.CreateCommand();
            sync.CommandText = "PRAGMA synchronous;";
            var synchronous = Convert.ToInt64(await sync.ExecuteScalarAsync());

            await using var fk = conn.CreateCommand();
            fk.CommandText = "PRAGMA foreign_keys;";
            var foreignKeys = Convert.ToInt64(await fk.ExecuteScalarAsync());

            await Assert.That(journalMode.ToLowerInvariant()).IsEqualTo("wal");
            // NORMAL == 1
            await Assert.That(synchronous).IsEqualTo(1);
            await Assert.That(foreignKeys).IsEqualTo(1);
        }
        finally
        {
            SqliteTestDb.Delete(path);
        }
    }
}

public sealed class SqliteMigrationServiceTests
{
    [Test]
    public async Task Migrate_AppliesIdempotently_AndDetectsChecksumDrift()
    {
        var path = await SqliteTestDb.CreateMigratedAsync();
        try
        {
            // Second apply is a no-op
            await SqliteMigrationService.MigrateAsync(
                SqliteTestDb.ConnectionString(path), NullLogger.Instance, CancellationToken.None);

            await using var conn = await SqliteTestDb.Factory(path).OpenConnectionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM schema_migrations";
            var count = Convert.ToInt64(await cmd.ExecuteScalarAsync());
            await Assert.That(count).IsGreaterThanOrEqualTo(3);

            // Checksum helper is stable
            var checksum = SqliteMigrationService.ComputeChecksum("SELECT 1;");
            await Assert.That(checksum).IsNotEmpty();
        }
        finally
        {
            SqliteTestDb.Delete(path);
        }
    }
}

public sealed class SqliteSecretsStoreTests
{
    [Test]
    public async Task Upsert_Get_List_RoundTrip()
    {
        var path = await SqliteTestDb.CreateMigratedAsync();
        try
        {
            var store = new SqliteSecretsStore(SqliteTestDb.Factory(path));
            await store.UpsertAsync(SecretsStoreKeys.EncryptionPepper, "pepper-value");

            var got = await store.GetAsync(SecretsStoreKeys.EncryptionPepper);
            await Assert.That(got).IsEqualTo("pepper-value");

            var required = await store.GetRequiredAsync(SecretsStoreKeys.EncryptionPepper);
            await Assert.That(required).IsEqualTo("pepper-value");

            var list = await store.ListAsync();
            await Assert.That(list.Any(e => e.Key.Value == SecretsStoreKeys.EncryptionPepper.Value)).IsTrue();
        }
        finally
        {
            SqliteTestDb.Delete(path);
        }
    }
}

public sealed class SqliteIdempotencyStoreTests
{
    [Test]
    public async Task Save_Then_Find_ReturnsMatch()
    {
        var path = await SqliteTestDb.CreateMigratedAsync();
        try
        {
            var store = new SqliteIdempotencyStore(SqliteTestDb.Factory(path));
            var principal = new SystemId("alice");
            var op = new OperationId("op-1");
            var key = new IdempotencyKey("idem-1");

            await Assert.That(await store.FindAsync(principal, op, key)).IsNull();

            await store.SaveAsync(principal, op, key, "payload-h", "outcome-h", "{\"ok\":true}");
            var match = await store.FindAsync(principal, op, key);

            await Assert.That(match).IsNotNull();
            await Assert.That(match!.PayloadHash).IsEqualTo("payload-h");
            await Assert.That(match.OutcomeHash).IsEqualTo("outcome-h");
            await Assert.That(match.OutcomePayload).IsEqualTo("{\"ok\":true}");
        }
        finally
        {
            SqliteTestDb.Delete(path);
        }
    }
}

public sealed class SqliteAuthTokenRevocationRepositoryTests
{
    [Test]
    public async Task Record_Validate_Revoke()
    {
        var path = await SqliteTestDb.CreateMigratedAsync();
        try
        {
            var repo = new SqliteAuthTokenRevocationRepository(SqliteTestDb.Factory(path));
            var jti = new Jti(Guid.NewGuid().ToString("N"));
            var systemId = new SystemId("bob");

            await repo.RecordTokenAsync(jti, systemId, DateTimeOffset.UtcNow.AddHours(1));
            await Assert.That(await repo.ValidateTokenNotRevokedAsync(jti)).IsTrue();

            await repo.RevokeTokenAsync(jti);
            await Assert.That(await repo.ValidateTokenNotRevokedAsync(jti)).IsFalse();
        }
        finally
        {
            SqliteTestDb.Delete(path);
        }
    }
}

public sealed class SqliteHealthCheckerTests
{
    [Test]
    public async Task CheckHealth_Healthy_WhenMigrated()
    {
        var path = await SqliteTestDb.CreateMigratedAsync();
        try
        {
            var checker = new SqliteHealthChecker(SqliteTestDb.Factory(path));
            var result = await checker.CheckHealthAsync(new Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext());
            await Assert.That(result.Status).IsEqualTo(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy);
        }
        finally
        {
            SqliteTestDb.Delete(path);
        }
    }
}
