using System.Globalization;
using Interfold.Bootstrapper.Cli;
using Interfold.Bootstrapper.Configuration;
using Interfold.Bootstrapper.Util;
using Interfold.Shared.Contracts.Configuration;

namespace Interfold.Bootstrapper.Phases;

/// <summary>Logical backup of live SQLite state; idempotent across reruns. Driven manually
/// (<c>interfold-bootstrap backup</c>) or by the systemd timer from
/// <see cref="SystemdInstallPhase"/>. Layout: <c>{backupDir}/sqlite/{timestamp}.db</c>.
/// Post-write prune keeps exactly <c>RetainCount</c> archives.</summary>
internal static class BackupPhase
{
    private static readonly string Phase = BootstrapCommand.Backup.ToPhaseLogName();

    internal static readonly string[] ValidComponents =
        Enum.GetValues<BackupDatabaseComponent>().Select(BackupDatabaseComponentExtensions.ToWireValue).ToArray();

    public static async Task<int> RunAsync(BootstrapOptions options, PhaseLogger logger, CancellationToken ct)
    {
        logger.PhaseStart(Phase);

        if (BackupDatabaseComponentExtensions.TryParse(options.BackupComponent) is not { } component)
        {
            logger.PhaseFail(Phase, PhaseFailureReasons.UnknownComponent);
            throw new InvalidOperationException(
                $"--component='{options.BackupComponent}' is invalid. Expected one of: {string.Join(", ", ValidComponents)}.");
        }

        var config = await PhaseArtifactLoader
            .LoadRequiredConfigAsync(options, logger, Phase, "Backup", ct)
            .ConfigureAwait(false);

        _ = PhaseArtifactLoader.LoadRequiredSecretsOrFail(options, logger, Phase, "Backup");

        var composeFile = PhaseArtifactLoader.RequireComposeFileOrFail(options, logger, Phase);

        var backupRoot = ResolveBackupRoot(options, config);
        var retainCount = options.BackupRetainOverride ?? config.Deployment.Backup.RetainCount;
        if (retainCount < 1)
        {
            logger.PhaseFail(Phase, PhaseFailureReasons.InvalidRetain);
            throw new InvalidOperationException(
                $"--retain={retainCount} is below the minimum of 1.");
        }
        logger.Info($"    backup root: {backupRoot} (retain {retainCount} per component)");

        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

        if (component is not (BackupDatabaseComponent.Sqlite or BackupDatabaseComponent.All))
        {
            logger.PhaseFail(Phase, PhaseFailureReasons.UnknownComponent);
            throw new InvalidOperationException(
                $"--component={component.ToWireValue()} is not supported. " +
                "Use --component=sqlite or --component=all.");
        }

        await BackupSqliteAsync(composeFile, options.OutputDir, backupRoot, timestamp, retainCount, logger, ct)
            .ConfigureAwait(false);

        logger.PhaseDone(Phase);
        return 0;
    }

    /// <summary>Precedence: <c>--backup-dir</c> CLI → <c>config.backup.directory</c> →
    /// <c>{outputDir}/backups</c>.</summary>
    internal static string ResolveBackupRoot(BootstrapOptions options, BootstrapConfig config)
    {
        if (!string.IsNullOrWhiteSpace(options.BackupDirOverride))
        {
            return Path.GetFullPath(options.BackupDirOverride);
        }
        if (!string.IsNullOrWhiteSpace(config.Deployment.Backup.Directory))
        {
            return Path.GetFullPath(config.Deployment.Backup.Directory);
        }
        return Path.Combine(options.OutputDir, "backups");
    }

    /// <summary>Canonical archive filename (relative to the component subdirectory).</summary>
    internal static string BuildArchiveFileName(BackupDatabaseComponent component, string timestamp)
    {
        return component switch
        {
            BackupDatabaseComponent.Sqlite => $"{timestamp}.db",
            _ => throw new InvalidOperationException($"Unknown component '{component}' (expected: sqlite)."),
        };
    }

    /// <summary>Absolute path to the live SQLite database under the bootstrapper output tree.</summary>
    internal static string ResolveSqliteDbPath(string outputDir)
        => Path.Combine(PublishPhase.ResolveSqliteDataHostDir(outputDir), ContainerMountPaths.InterfoldSqliteDbFileName);

    internal static void PruneComponent(string componentDir, BackupDatabaseComponent component, int retainCount, PhaseLogger logger)
    {
        var pattern = component switch
        {
            BackupDatabaseComponent.Sqlite => BackupStoragePaths.SqliteArchivePattern,
            _ => throw new InvalidOperationException($"Unknown component '{component}'."),
        };
        if (!Directory.Exists(componentDir))
        {
            return;
        }

        var files = new DirectoryInfo(componentDir)
            .EnumerateFiles(pattern, SearchOption.TopDirectoryOnly)
            .ToList();

        foreach (var stale in BackupRetention.Prune(files, retainCount))
        {
            try
            {
                stale.Delete();
                logger.Info($"    {component}: pruned {stale.Name}");
            }
            catch (Exception ex)
            {
                logger.Warn($"failed to delete {stale.FullName}: {ex.Message}");
            }
        }
    }

    private static async Task BackupSqliteAsync(
        string composeFile, string outputDir, string backupRoot, string timestamp, int retainCount,
        PhaseLogger logger, CancellationToken ct)
    {
        // The container and this Windows process cannot share the bind-mounted file.
        var apiWasRunning = await StopApiIfRunningAsync(composeFile, logger, ct).ConfigureAwait(false);
        try
        {
            await WriteSqliteArchiveAsync(outputDir, backupRoot, timestamp, retainCount, logger, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            if (apiWasRunning)
            {
                logger.Info($"    sqlite: starting {ComposeServices.InterfoldApi}");
                await DockerCompose.StartAsync(composeFile, [ComposeServices.InterfoldApi], ct).ConfigureAwait(false);
            }
        }
    }

    private static async Task<bool> StopApiIfRunningAsync(
        string composeFile, PhaseLogger logger, CancellationToken ct)
    {
        var ps = await DockerCompose.PsAsync(composeFile, ComposeServices.InterfoldApi, ct: ct).ConfigureAwait(false);
        if (ps.ExitCode != 0 || string.IsNullOrWhiteSpace(ps.StdOut))
            return false;

        logger.Info($"    sqlite: stopping {ComposeServices.InterfoldApi}");
        var stop = await DockerCompose.StopAsync(composeFile, [ComposeServices.InterfoldApi], ct: ct).ConfigureAwait(false);
        if (stop.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"docker compose stop {ComposeServices.InterfoldApi} exited {stop.ExitCode}: {stop.StdErr.Trim()}");
        }

        return true;
    }

    private static async Task WriteSqliteArchiveAsync(
        string outputDir, string backupRoot, string timestamp, int retainCount,
        PhaseLogger logger, CancellationToken ct)
    {
        var sourcePath = ResolveSqliteDbPath(outputDir);
        if (!File.Exists(sourcePath))
        {
            logger.PhaseFail(Phase, PhaseFailureReasons.MissingSqliteDatabase);
            throw new InvalidOperationException(
                $"SQLite database not found at {sourcePath}. Run bootstrap (db-init) first.");
        }

        var componentDir = Path.Combine(backupRoot, BackupStoragePaths.SqliteDir);
        Directory.CreateDirectory(componentDir);
        var archivePath = Path.Combine(componentDir, BuildArchiveFileName(BackupDatabaseComponent.Sqlite, timestamp));

        logger.Info($"    sqlite: online backup {sourcePath} -> {archivePath}");
        await SqliteOnlineBackup.BackupAsync(sourcePath, archivePath, ct).ConfigureAwait(false);

        var size = new FileInfo(archivePath).Length;
        if (size == 0)
        {
            logger.PhaseFail(Phase, PhaseFailureReasons.EmptySqliteArchive);
            File.Delete(archivePath);
            throw new InvalidOperationException($"SQLite backup produced an empty file at {archivePath}.");
        }
        logger.Info($"    sqlite: wrote {FormatBytes(size)}");
        PruneComponent(componentDir, BackupDatabaseComponent.Sqlite, retainCount, logger);
    }

    private static string FormatBytes(long bytes)
    {
        const long Kib = 1024L;
        const long Mib = Kib * 1024L;
        const long Gib = Mib * 1024L;
        if (bytes >= Gib) return $"{bytes / (double)Gib:F2} GiB";
        if (bytes >= Mib) return $"{bytes / (double)Mib:F2} MiB";
        if (bytes >= Kib) return $"{bytes / (double)Kib:F2} KiB";
        return $"{bytes} B";
    }
}
