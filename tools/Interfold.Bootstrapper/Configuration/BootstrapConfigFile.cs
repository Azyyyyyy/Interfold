using System.Text.Json;
using Interfold.Bootstrapper.Cli;
using Interfold.Bootstrapper.Phases;

namespace Interfold.Bootstrapper.Configuration;

/// <summary>Shared load path: schema migration, persist, deserialize, derive defaults.</summary>
internal static class BootstrapConfigFile
{
    internal static async Task<BootstrapConfig> LoadAsync(
        string configPath,
        PhaseLogger logger,
        CancellationToken ct)
    {
        var json = await File.ReadAllTextAsync(configPath, ct).ConfigureAwait(false);
        var migration = ConfigSchemaMigrator.MigrateIfNeeded(json, configPath, logger);
        if (migration.DidMigrate)
        {
            await ConfigSchemaMigrator.PersistMigratedAsync(
                migration.OriginalJson!,
                migration.Json,
                configPath,
                ct).ConfigureAwait(false);
        }

        var config = JsonSerializer.Deserialize(migration.Json, BootstrapJsonContext.Default.BootstrapConfig)
            ?? throw new InvalidOperationException($"Failed to parse {configPath} (returned null).");

        ConfigPhase.ResolveDerivedDefaults(config);

        if (migration.DidMigrate && migration.V1Snapshot is not null)
        {
            PublicEndpointUrls.LogMigrationNotice(config, migration.V1Snapshot, configPath, logger);
        }

        return config;
    }
}
