using Interfold.Bootstrapper.Cli;
using Interfold.Bootstrapper.Configuration;
using Interfold.Bootstrapper.Util;

namespace Interfold.Bootstrapper.Phases;

internal static class SelfUpdatePhase
{
    internal const int CheckUpdateAvailableExitCode = 2;

    private static readonly string Phase = BootstrapCommand.UpdateSelf.ToPhaseLogName();

    public static async Task<int> RunAsync(BootstrapOptions options, PhaseLogger logger, CancellationToken ct)
    {
        logger.PhaseStart(Phase);
        SelfUpdatePhaseCore.RequireSupportedHost(logger);

        BootstrapConfig? config = null;
        if (!string.IsNullOrWhiteSpace(options.ConfigPath) && File.Exists(options.ConfigPath))
        {
            config = await PhaseArtifactLoader
                .LoadRequiredConfigAsync(options, logger, Phase, "update-self", ct)
                .ConfigureAwait(false);
        }

        var channel = options.SelfUpdateChannelOverride
            ?? config?.Deployment.Update.Bootstrapper.Channel
            ?? BootstrapperReleaseChannel.Stable;

        var result = await SelfUpdatePhaseCore.RunAsync(
            new SelfUpdateRequest(
                Channel: channel,
                CheckOnly: options.SelfUpdateCheckOnly,
                Force: options.SelfUpdateForce,
                Rollback: options.SelfUpdateRollback,
                OutputDir: options.OutputDir),
            logger,
            ct).ConfigureAwait(false);

        if (result == SelfUpdateResult.UpdateAvailable)
        {
            logger.PhaseDone(Phase);
            return CheckUpdateAvailableExitCode;
        }

        logger.PhaseDone(Phase);
        return result == SelfUpdateResult.Failed ? 1 : 0;
    }

    internal static async Task<SelfUpdateResult> RunForBootstrapAsync(
        BootstrapConfig config,
        BootstrapOptions options,
        PhaseLogger logger,
        CancellationToken ct)
    {
        if (options.SkipSelfUpdate || !config.Deployment.Update.Bootstrapper.ResolveUpdateOnBootstrap())
        {
            return SelfUpdateResult.UpToDate;
        }

        SelfUpdatePhaseCore.RequireSupportedHost(logger);
        return await SelfUpdatePhaseCore.RunAsync(
            new SelfUpdateRequest(
                Channel: config.Deployment.Update.Bootstrapper.Channel,
                CheckOnly: false,
                Force: false,
                Rollback: false,
                OutputDir: options.OutputDir),
            logger,
            ct).ConfigureAwait(false);
    }
}

internal enum SelfUpdateResult
{
    UpToDate,
    Updated,
    RolledBack,
    UpdateAvailable,
    Failed,
}

internal sealed record SelfUpdateRequest(
    BootstrapperReleaseChannel Channel,
    bool CheckOnly,
    bool Force,
    bool Rollback,
    string OutputDir);

internal static class SelfUpdatePhaseCore
{
    internal static async Task<SelfUpdateResult> RunAsync(
        SelfUpdateRequest request,
        PhaseLogger logger,
        CancellationToken ct)
    {
        var binaryPath = ResolveBinaryPath();
        if (request.Rollback)
        {
            return Rollback(binaryPath, logger);
        }

        using var client = BootstrapperReleaseClient.CreateDefault();
        var rid = BootstrapperRid.DetectHostRid();
        string remoteVersion;
        try
        {
            remoteVersion = await client.FetchRemoteVersionAsync(request.Channel, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.Error($"failed to fetch remote version: {ex.Message}");
            return SelfUpdateResult.Failed;
        }

        if (!BootstrapperVersion.IsUpdateAvailable(remoteVersion, request.Force))
        {
            logger.Info($"    bootstrapper is up to date ({BootstrapperVersion.InformationalVersion})");
            return SelfUpdateResult.UpToDate;
        }

        if (request.CheckOnly)
        {
            logger.Info(
                $"    update available: {BootstrapperVersion.InformationalVersion} -> {remoteVersion} " +
                $"({request.Channel.ToWireValue()})");
            return SelfUpdateResult.UpdateAvailable;
        }

        var binaryDir = Path.GetDirectoryName(binaryPath)
            ?? throw new InvalidOperationException($"Could not resolve directory for {binaryPath}.");
        var fileName = Path.GetFileName(binaryPath);
        var assetExt = BootstrapperRid.IsZipAsset(rid) ? ".zip" : ".tar.gz";
        var downloadPath = Path.Combine(binaryDir, $"{fileName}.download{assetExt}");
        var newBinaryPath = Path.Combine(binaryDir, $"{fileName}.new");

        try
        {
            logger.Info(
                $"    downloading {request.Channel.ToWireValue()} release {remoteVersion} for {rid}");
            await client.DownloadVerifiedReleaseAssetAsync(request.Channel, rid, downloadPath, ct)
                .ConfigureAwait(false);
            BootstrapperReleaseClient.ExtractBootstrapperFromArchive(downloadPath, newBinaryPath);
            SetExecutable(newBinaryPath);

            var oldBinaryPath = SiblingOldPath(binaryPath);
            if (File.Exists(binaryPath))
            {
                File.Move(binaryPath, oldBinaryPath, overwrite: true);
            }

            File.Move(newBinaryPath, binaryPath, overwrite: true);
            WriteVersionStamp(request.OutputDir, remoteVersion);
            logger.Info(
                $"    bootstrapper updated: {BootstrapperVersion.InformationalVersion} -> {remoteVersion}");
            return SelfUpdateResult.Updated;
        }
        catch (Exception ex)
        {
            logger.Error($"bootstrapper self-update failed: {ex.Message}");
            if (File.Exists(newBinaryPath))
            {
                try { File.Delete(newBinaryPath); } catch { /* best-effort cleanup */ }
            }

            return SelfUpdateResult.Failed;
        }
        finally
        {
            if (File.Exists(downloadPath))
            {
                try { File.Delete(downloadPath); } catch { /* best-effort cleanup */ }
            }
        }
    }

    internal static SelfUpdateResult Rollback(string binaryPath, PhaseLogger logger)
    {
        var binaryDir = Path.GetDirectoryName(binaryPath)
            ?? throw new InvalidOperationException($"Could not resolve directory for {binaryPath}.");
        var oldBinaryPath = SiblingOldPath(binaryPath);
        if (!File.Exists(oldBinaryPath))
        {
            logger.Error($"no previous bootstrapper binary at {oldBinaryPath}");
            return SelfUpdateResult.Failed;
        }

        var backupCurrent = Path.Combine(binaryDir, $"{Path.GetFileName(binaryPath)}.failed");
        if (File.Exists(binaryPath))
        {
            File.Move(binaryPath, backupCurrent, overwrite: true);
        }

        File.Move(oldBinaryPath, binaryPath, overwrite: true);
        SetExecutable(binaryPath);
        logger.Info($"    bootstrapper rolled back to {oldBinaryPath}");
        return SelfUpdateResult.RolledBack;
    }

    internal static void RequireSupportedHost(PhaseLogger logger)
    {
        if (OperatingSystem.IsLinux())
        {
            if (PrerequisitesPhase.IsRunningAsRoot())
            {
                return;
            }

            logger.Error("bootstrapper self-update requires root (run with sudo).");
            throw new InvalidOperationException("bootstrapper self-update requires root.");
        }

        if (OperatingSystem.IsWindows())
        {
            // LeastPrivilege Task Scheduler / per-user installs: writable binary dir is enough.
            return;
        }

        throw new PlatformNotSupportedException(
            "Bootstrapper self-update requires Linux or Windows.");
    }

    internal static string ResolveBinaryPath()
    {
        if (!string.IsNullOrWhiteSpace(Environment.ProcessPath))
        {
            return Path.GetFullPath(Environment.ProcessPath);
        }

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, HostPaths.BootstrapperFileName));
    }

    private static string SiblingOldPath(string binaryPath)
        => Path.Combine(
            Path.GetDirectoryName(binaryPath)
            ?? throw new InvalidOperationException($"Could not resolve directory for {binaryPath}."),
            $"{Path.GetFileName(binaryPath)}.old");

    private static void WriteVersionStamp(string outputDir, string version)
    {
        try
        {
            var stampPath = Path.Combine(outputDir, ".bootstrapper-version");
            Directory.CreateDirectory(outputDir);
            File.WriteAllText(stampPath, version.Trim());
        }
        catch
        {
            // best-effort stamp for rollback diagnostics
        }
    }

    private static void SetExecutable(string path)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }
}
