using System.Text.Json;
using Interfold.Bootstrapper.Cli;
using Interfold.Bootstrapper.Configuration;
using Interfold.Contracts.Configuration;

namespace Interfold.Bootstrapper.Phases;

/// <summary>
/// The two "look up an artifact on disk, log its resolved path, or fail-fast with the
/// canonical phase-failure reason" helpers every phase used to repeat verbatim. Consolidates:
/// <list type="bullet">
///   <item><see cref="LoadRequiredConfigAsync"/> — resolve the config path, error out with
///     the <c>MissingConfig</c> reason if it's absent, else stream-deserialize it.</item>
///   <item><see cref="RequireComposeFileOrFail"/> — locate the emitted compose file, error
///     out with the <c>NoComposeFile</c> reason if it's absent, else log its resolved path
///     and return it.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// Previously every phase that needed either artifact reimplemented the same 8-15 line block,
/// with per-site drift in error wording ("Backup requires..." vs "restore requires..." vs
/// "update-images requires...") that we deliberately preserve here through the
/// <c>commandVerb</c> parameter so the operator-visible messages don't change.
/// </para>
/// <para>
/// Both helpers deliberately emit through <see cref="PhaseLogger.PhaseFail"/> before throwing
/// so failure telemetry and structured logs stay consistent. <see cref="DatabaseInitPhase"/>
/// used to skip the <c>PhaseFail</c> call on missing compose; migrating it here also folds it
/// into the same failure-emission contract as everyone else (benign observability upgrade).
/// </para>
/// </remarks>
internal static class PhaseArtifactLoader
{
    /// <summary>
    /// Resolves the bootstrap config path (via <see cref="BootstrapArtifactPaths.ResolveConfigPath"/>),
    /// requires it to exist, and stream-deserializes it. On the missing-file path emits
    /// <see cref="PhaseFailureReasons.MissingConfig"/> and throws with an operator-friendly
    /// message that includes the resolved path and the "Run `bootstrap` first" recovery hint.
    /// </summary>
    /// <param name="commandVerb">
    /// The verb used at the start of the error message ("Backup", "restore", "update-images",
    /// "install-service"). Preserves the exact per-command wording each phase historically used.
    /// </param>
    public static async Task<BootstrapConfig> LoadRequiredConfigAsync(
        BootstrapOptions options,
        PhaseLogger logger,
        string phase,
        string commandVerb,
        CancellationToken ct)
    {
        var configPath = BootstrapArtifactPaths.ResolveConfigPath(options);
        if (!File.Exists(configPath))
        {
            logger.PhaseFail(phase, PhaseFailureReasons.MissingConfig);
            throw new InvalidOperationException(
                $"{commandVerb} requires a populated bootstrap config at {configPath}. " +
                "Run `bootstrap` first.");
        }

        await using var stream = File.OpenRead(configPath);
        return await JsonSerializer.DeserializeAsync(
            stream, BootstrapJsonContext.Default.BootstrapConfig, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Failed to parse {configPath}.");
    }

    /// <summary>
    /// Locates the emitted compose file under <paramref name="options"/>'s output dir (via
    /// <see cref="BootstrapArtifactPaths.FindComposeFile"/>). Emits
    /// <see cref="PhaseFailureReasons.NoComposeFile"/> and throws with the canonical
    /// operator message when it's absent, otherwise logs the resolved path and returns it.
    /// </summary>
    public static string RequireComposeFileOrFail(
        BootstrapOptions options,
        PhaseLogger logger,
        string phase)
    {
        var composeFile = BootstrapArtifactPaths.FindComposeFile(options.OutputDir);
        if (composeFile is null)
        {
            logger.PhaseFail(phase, PhaseFailureReasons.NoComposeFile);
            throw new InvalidOperationException(
                $"docker-compose.yaml not found under {options.OutputDir}. Run `bootstrap publish` first.");
        }
        logger.Info($"    using compose file {composeFile}");
        return composeFile;
    }
}
