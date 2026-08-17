using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Configuration;
using Interfold.Shared.Contracts.Secrets;
using Interfold.Infrastructure.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Core.Interfaces;

namespace Interfold.IntegrationTests.Shared.TestServices;

/// <summary>
/// Standalone fixture (no Aspire, no Docker) that boots the API against a per-session
/// temp-file SQLite database. Migrates + seeds required secrets once before the first host build.
/// </summary>
public sealed class SqliteWebFactoryFixture : IWebFactoryFixture, IAsyncInitializer, IAsyncDisposable
{
    private string? _dbPath;

    public InterfoldWebApplicationFactory Factory { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"interfold-sqlite-fixture-{Guid.NewGuid():N}.db");
        var cs = $"Data Source={_dbPath}";

        await SqliteMigrationService.MigrateAsync(cs, NullLogger.Instance, CancellationToken.None);

        var store = new SqliteSecretsStore(new SqliteConnectionFactory(cs));
        await store.UpsertAsync(SecretsStoreKeys.EncryptionPepper, "TEST");
        await store.UpsertAsync(SecretsStoreKeys.AuthJwtEs256PrivatePem, TestDbCredentials.JwtEs256PrivateKeyPem);
        await store.UpsertAsync(SecretsStoreKeys.AuthDeepLinkSecret, TestDbCredentials.DeepLinkSecret);
        await store.UpsertAsync(SecretsStoreKeys.AuthJwtRsa256PrivatePem, TestDbCredentials.JwtRsa256PrivateKeyPem);

        Factory = CreatePrivateFactory();
    }

    public InterfoldWebApplicationFactory CreatePrivateFactory()
    {
        if (_dbPath is null)
        {
            throw new InvalidOperationException("SqliteWebFactoryFixture.InitializeAsync has not run yet.");
        }

        var cs = $"Data Source={_dbPath}";
        return new InterfoldWebApplicationFactory(PersistenceMode.Sqlite, "sqlite")
            .WithConfiguration(OctoconEnvKeys.SqliteConnection, cs);
    }

    public async ValueTask DisposeAsync()
    {
        if (Factory is not null)
        {
            await Factory.DisposeAsync();
        }

        if (_dbPath is not null)
        {
            foreach (var p in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            {
                try { if (File.Exists(p)) File.Delete(p); } catch { /* best-effort */ }
            }
        }
    }
}
