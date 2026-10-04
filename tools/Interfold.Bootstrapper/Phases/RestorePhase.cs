using Interfold.Bootstrapper.Cli;
using Interfold.Bootstrapper.Configuration;
using Interfold.Bootstrapper.Util;
using Interfold.Shared.Contracts.Configuration;

namespace Interfold.Bootstrapper.Phases;

/// <summary>Inverse of <see cref="BackupPhase"/>. Sqlite stops the API, replaces the host
/// <c>.db</c>, and restarts. Destructive; gated by interactive confirmation or
/// <see cref="BootstrapOptions.RestoreForce"/>.</summary>
internal static class RestorePhase
{
    private static readonly string Phase = BootstrapCommand.Restore.ToPhaseLogName();

    public static async Task<int> RunAsync(BootstrapOptions options, PhaseLogger logger, CancellationToken ct)
    {
        logger.PhaseStart(Phase);

        var config = await PhaseArtifactLoader
            .LoadRequiredConfigAsync(options, logger, Phase, "restore", ct)
            .ConfigureAwait(false);

        _ = PhaseArtifactLoader.LoadRequiredSecretsOrFail(options, logger, Phase, "Restore");

        var composeFile = PhaseArtifactLoader.RequireComposeFileOrFail(options, logger, Phase);

        var backupRoot = BackupPhase.ResolveBackupRoot(options, config);
        var sqliteArchive = ResolveArchive(options, backupRoot, logger);
        if (sqliteArchive is null)
        {
            logger.PhaseFail(Phase, PhaseFailureReasons.NoArchives);
            throw new InvalidOperationException(
                "restore requires --restore-sqlite or --restore-latest " +
                $"(with archives under {backupRoot}/sqlite/).");
        }

        // Non-interactive callers MUST pass --force so a stray systemd unit or CI job
        // can't wipe a data volume.
        if (!options.RestoreForce)
        {
            if (options.NonInteractive || Console.IsInputRedirected)
            {
                logger.PhaseFail(Phase, PhaseFailureReasons.ConfirmationRequired);
                throw new InvalidOperationException(
                    "restore is destructive and requires --force in non-interactive mode. " +
                    "Re-run interactively without --non-interactive to type 'y' at the confirmation prompt, " +
                    "or pass --force to skip the prompt.");
            }
            Console.WriteLine();
            Console.WriteLine("*** DESTRUCTIVE OPERATION ***");
            Console.WriteLine("This will overwrite the current database contents with the archive on disk.");
            Console.WriteLine($"  sqlite   <- {sqliteArchive}");
            Console.Write("Type 'y' to proceed, anything else to abort: ");
            var answer = Console.ReadLine();
            if (!string.Equals(answer?.Trim(), "y", StringComparison.OrdinalIgnoreCase))
            {
                logger.PhaseSkip(Phase, PhaseFailureReasons.Skip.OperatorAborted);
                return 0;
            }
        }

        await RestoreSqliteAsync(composeFile, options.OutputDir, sqliteArchive, logger, ct).ConfigureAwait(false);

        logger.PhaseDone(Phase);
        return 0;
    }

    /// <summary>Explicit path beats <c>--restore-latest</c>. Returns null when no archive was chosen.</summary>
    internal static string? ResolveArchive(
        BootstrapOptions options, string backupRoot, PhaseLogger logger)
    {
        string? sqlite = options.RestoreSqliteArchive;

        if (options.RestoreLatest && sqlite is null)
        {
            sqlite = BackupStoragePaths.LatestFile(
                Path.Combine(backupRoot, BackupStoragePaths.SqliteDir),
                BackupStoragePaths.SqliteArchivePattern)?.FullName;
            if (sqlite is not null) logger.Info($"    resolved --restore-latest sqlite: {sqlite}");
        }

        if (sqlite is not null && !File.Exists(sqlite))
        {
            throw new InvalidOperationException($"SQLite archive not found: {sqlite}");
        }
        return sqlite;
    }

    private static async Task RestoreSqliteAsync(
        string composeFile, string outputDir, string archivePath,
        PhaseLogger logger, CancellationToken ct)
    {
        var targetPath = BackupPhase.ResolveSqliteDbPath(outputDir);
        var targetDir = Path.GetDirectoryName(targetPath)!;
        Directory.CreateDirectory(targetDir);

        logger.Info($"    sqlite: stopping {ComposeServices.InterfoldApi}");
        var stop = await DockerCompose.StopAsync(composeFile, [ComposeServices.InterfoldApi], ct: ct).ConfigureAwait(false);
        if (stop.ExitCode != 0)
        {
            logger.Warn($"docker compose stop {ComposeServices.InterfoldApi} exited {stop.ExitCode}: {stop.StdErr.Trim()}");
        }

        foreach (var sidecar in new[] { targetPath, targetPath + "-wal", targetPath + "-shm" })
        {
            try { if (File.Exists(sidecar)) File.Delete(sidecar); }
            catch (Exception ex) { logger.Warn($"failed to delete {sidecar}: {ex.Message}"); }
        }

        logger.Info($"    sqlite: copying {archivePath} -> {targetPath}");
        File.Copy(archivePath, targetPath, overwrite: true);

        logger.Info($"    sqlite: starting {ComposeServices.InterfoldApi}");
        await DockerCompose.UpCheckedAsync(composeFile, [ComposeServices.InterfoldApi], logger, ct).ConfigureAwait(false);
        logger.Info("    sqlite: restore complete");
    }
}
