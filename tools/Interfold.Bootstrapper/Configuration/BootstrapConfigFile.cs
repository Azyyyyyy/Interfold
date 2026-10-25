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

        var config = JsonSerializer.Deserialize(json, BootstrapJsonContext.Default.BootstrapConfig)
            ?? throw new InvalidOperationException($"Failed to parse {configPath} (returned null).");

        ConfigPhase.ResolveDerivedDefaults(config);

        return config;
    }
}
