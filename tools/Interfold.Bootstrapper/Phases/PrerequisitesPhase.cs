using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text.Json;
using Interfold.Bootstrapper.Cli;
using Interfold.Bootstrapper.Configuration;
using Interfold.Bootstrapper.Util;
using Interfold.Shared.Contracts.Enums;

namespace Interfold.Bootstrapper.Phases;

/// <summary>Phase 1 — verifies platform support and installs Docker Engine + Compose
/// (Linux: distro packages + AIO sysctl; Windows: Docker Desktop via winget/choco).</summary>
internal static partial class PrerequisitesPhase
{
    // Seastar's own startup error text; keep aligned with scripts/docker/ensure-host-aio.sh
    // and tests/integration/Interfold.IntegrationTests.Shared/TestServices/HostAioPrerequisite.cs.
    private const int AioPerNodeMin = 66_563;
    private const int AioPerNodeRecommended = 116_562;
    private const int AioHeadroom = 50_000;
    private const string AioSysctlPath = "/proc/sys/fs/aio-max-nr";
    private const string SysctlDropIn = "/etc/sysctl.d/99-interfold.conf";

    /// <summary>Raw-string overload for the pre-validation peek in
    /// <see cref="PeekScyllaNodeCountAsync"/>.</summary>
    internal static int ResolveScyllaNodeCount(string? cqlBackendWire)
        => ResolveScyllaNodeCount(CqlBackendMapping.ToDatabaseMode(CqlBackendMapping.ParseWire(cqlBackendWire)));

    internal static int ResolveScyllaNodeCount(DatabaseMode databaseMode) => databaseMode switch
    {
        DatabaseMode.Multi => 7,
        DatabaseMode.Cassandra => 0,
        _ => 1,
    };

    public static async Task RunAsync(BootstrapOptions options, PhaseLogger logger, CancellationToken ct)
    {
        string Phase = BootstrapPhase.Prereqs.ToWireName();
        logger.PhaseStart(Phase);

        if (OperatingSystem.IsWindows())
        {
            await RunWindowsAsync(options, logger, ct).ConfigureAwait(false);
            logger.PhaseDone(Phase);
            return;
        }

        EnsureLinux(logger);
        EnsureRoot(logger);

        var distro = DistroInfo.Read();
        logger.Info($"    detected distro: {distro.PrettyName ?? distro.Id} (family={distro.Family})");

        if (distro.Family == DistroFamily.Unknown)
        {
            logger.PhaseFail(Phase, PhaseFailureReasons.UnsupportedDistro);
            throw new InvalidOperationException(
                $"Unsupported Linux distribution '{distro.Id}'. Supported families: Debian/Ubuntu, RHEL/Fedora. " +
                "See docs/SELF_HOSTING.md for tested distros.");
        }

        await EnsureDockerAsync(distro, logger, ct).ConfigureAwait(false);
        await EnsureOpenSslAsync(distro, logger, ct).ConfigureAwait(false);

        // Tolerant peek — defaults to single-node baseline on missing/malformed/unrecognised.
        // ConfigPhase still owns full schema validation.
        var scyllaNodes = await PeekScyllaNodeCountAsync(options, logger, ct).ConfigureAwait(false);
        await EnsureAioLimitAsync(scyllaNodes, logger, ct).ConfigureAwait(false);

        logger.PhaseDone(Phase);
    }

    private static async Task<int> PeekScyllaNodeCountAsync(BootstrapOptions options, PhaseLogger logger, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(options.ConfigPath) || !File.Exists(options.ConfigPath))
        {
            return ResolveScyllaNodeCount(null);
        }

        try
        {
            await using var stream = File.OpenRead(options.ConfigPath);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            if (doc.RootElement.TryGetProperty("datastores", out var datastores)
                && datastores.TryGetProperty("cql", out var cql)
                && cql.TryGetProperty("backend", out var backendElement)
                && backendElement.ValueKind == JsonValueKind.String)
            {
                return ResolveScyllaNodeCount(backendElement.GetString());
            }
        }
        catch (Exception ex)
        {
            // ConfigPhase will surface a useful error against the same file next.
            logger.Warn($"could not pre-read datastores.cql.backend from {options.ConfigPath} for AIO sizing ({ex.GetType().Name}); defaulting to single-node baseline.");
        }

        return ResolveScyllaNodeCount(null);
    }

    private static void EnsureLinux(PhaseLogger logger)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return;
        }
        logger.PhaseFail(BootstrapPhase.Prereqs.ToWireName(), PhaseFailureReasons.NonLinuxHost);
        throw new InvalidOperationException(
            "The bootstrapper supports Linux and Windows. macOS is not supported; " +
            "for local development on this OS use `aspire run` from hosts/Interfold.AppHost instead.");
    }

    private static void EnsureRoot(PhaseLogger logger)
    {
        if (NativeMethods.geteuid() == 0) return;

        logger.PhaseFail(BootstrapPhase.Prereqs.ToWireName(), PhaseFailureReasons.NonRoot);
        throw new InvalidOperationException(
            "Run the bootstrapper with sudo. Installing Docker, writing to /etc/sysctl.d, " +
            "and editing the system trust store all require root.");
    }

    private static partial class NativeMethods
    {
        [LibraryImport("libc")]
        internal static partial uint geteuid();
    }

    private static async Task EnsureDockerAsync(DistroInfo distro, PhaseLogger logger, CancellationToken ct)
    {
        if (await ProcessRunner.ExistsOnPathAsync("docker", ct).ConfigureAwait(false))
        {
            var compose = await ProcessRunner.RunAsync("docker", ["compose", "version"], ct: ct).ConfigureAwait(false);
            if (compose.ExitCode == 0)
            {
                logger.Info("    docker + compose plugin already present");
                return;
            }
            logger.Warn("docker found but `docker compose` is missing; installing compose plugin.");
        }

        logger.Info("    installing docker engine + compose plugin from docker.com...");
        // Stock distro repos ship no docker-compose-plugin; installing from Docker's official
        // repo per https://docs.docker.com/engine/install/ is the only path.
        switch (distro.Family)
        {
            case DistroFamily.Debian:
                await ConfigureDockerAptRepoAsync(distro, logger, ct).ConfigureAwait(false);
                await RunAptInstallAsync(
                    ["docker-ce", "docker-ce-cli", "containerd.io", "docker-buildx-plugin",
                     "docker-compose-plugin", "ca-certificates"],
                    logger, ct).ConfigureAwait(false);
                break;
            case DistroFamily.RedHat:
                await ConfigureDockerDnfRepoAsync(distro, logger, ct).ConfigureAwait(false);
                await RunDnfInstallAsync(
                    ["docker-ce", "docker-ce-cli", "containerd.io", "docker-buildx-plugin",
                     "docker-compose-plugin", "ca-certificates"],
                    logger, ct).ConfigureAwait(false);
                break;
            default:
                throw new InvalidOperationException($"Cannot install Docker on distro family {distro.Family}.");
        }

        // Wrap in try/catch because Process.Start throws Win32Exception on missing binary
        // (Alpine/minimal containers ship without systemd).
        try
        {
            await ProcessRunner.RunAsync("systemctl", ["enable", "--now", "docker"], ct: ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.Warn($"could not run `systemctl enable --now docker` ({ex.GetType().Name}: {ex.Message}); " +
                        "start dockerd manually if it isn't already running.");
        }
    }

    private static async Task ConfigureDockerAptRepoAsync(DistroInfo distro, PhaseLogger logger, CancellationToken ct)
    {
        // Docker ships apt repos for ubuntu + debian only; downstream debian-likes fall back.
        var dockerDistro = ResolveDebianFamilyDockerDistro(distro);
        var codename = distro.VersionCodename ?? throw new InvalidOperationException(
            $"Could not determine VERSION_CODENAME for {distro.PrettyName ?? distro.Id}. " +
            "The bootstrapper needs this to choose the correct Docker apt suite.");

        var arch = await ResolveDpkgArchAsync(ct).ConfigureAwait(false);

        logger.Info($"    configuring apt repo: download.docker.com/linux/{dockerDistro} suite={codename} arch={arch}");

        // Standard third-party keyring location on Debian 12+ / Ubuntu 22.04+.
        var mkKeyring = await ProcessRunner.RunAsync(
            "install", ["-m", "0755", "-d", "/etc/apt/keyrings"], ct: ct).ConfigureAwait(false);
        if (mkKeyring.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Could not prepare /etc/apt/keyrings (exit {mkKeyring.ExitCode}): {mkKeyring.StdErr.Trim()}");
        }

        // In-process download so the phase doesn't depend on curl being installed.
        const string KeyringPath = "/etc/apt/keyrings/docker.asc";
        var keyUrl = $"https://download.docker.com/linux/{dockerDistro}/gpg";
        await DownloadFileAsync(keyUrl, KeyringPath, ct).ConfigureAwait(false);
        var chmod = await ProcessRunner.RunAsync("chmod", ["a+r", KeyringPath], ct: ct).ConfigureAwait(false);
        if (chmod.ExitCode != 0)
        {
            // apt-get update surfaces a clearer message if the file really isn't readable.
            logger.Warn($"chmod a+r {KeyringPath} exited {chmod.ExitCode}: {chmod.StdErr.Trim()}");
        }

        var sourcesLine =
            $"deb [arch={arch} signed-by={KeyringPath}] https://download.docker.com/linux/{dockerDistro} {codename} stable\n";
        await File.WriteAllTextAsync("/etc/apt/sources.list.d/docker.list", sourcesLine, ct).ConfigureAwait(false);
    }

    private static async Task ConfigureDockerDnfRepoAsync(DistroInfo distro, PhaseLogger logger, CancellationToken ct)
    {
        // .repo file is self-contained (GPG key URL + signature settings) → no keyring step.
        var dockerDistro = ResolveRedHatFamilyDockerDistro(distro);
        var repoUrl = $"https://download.docker.com/linux/{dockerDistro}/docker-ce.repo";
        const string RepoPath = "/etc/yum.repos.d/docker-ce.repo";

        logger.Info($"    configuring dnf repo: {repoUrl}");
        await DownloadFileAsync(repoUrl, RepoPath, ct).ConfigureAwait(false);
    }

    private static string ResolveDebianFamilyDockerDistro(DistroInfo distro)
    {
        var id = distro.Id.ToLowerInvariant();
        if (id is "ubuntu" or "debian") return id;
        var likes = (distro.IdLike ?? string.Empty).ToLowerInvariant();
        if (likes.Contains("ubuntu")) return "ubuntu";
        if (likes.Contains("debian")) return "debian";
        // Most modern debian-likes are ubuntu-based.
        return "ubuntu";
    }

    private static string ResolveRedHatFamilyDockerDistro(DistroInfo distro)
    {
        var id = distro.Id.ToLowerInvariant();
        if (id is "fedora" or "rhel" or "centos") return id;
        var likes = (distro.IdLike ?? string.Empty).ToLowerInvariant();
        if (likes.Contains("fedora")) return "fedora";
        if (likes.Contains("rhel") || likes.Contains("centos") || likes.Contains("rocky") || likes.Contains("almalinux"))
        {
            return "rhel";
        }
        return "rhel";
    }

    private static async Task<string> ResolveDpkgArchAsync(CancellationToken ct)
    {
        try
        {
            var result = await ProcessRunner.RunAsync("dpkg", ["--print-architecture"], ct: ct).ConfigureAwait(false);
            if (result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.StdOut))
            {
                return result.StdOut.Trim();
            }
        }
        catch
        {
            // dpkg is base on debian/ubuntu; fall through and default to amd64.
        }
        // arm64 operators can hand-drop their own /etc/apt/sources.list.d/docker.list and re-run.
        return "amd64";
    }

    private static async Task DownloadFileAsync(string url, string destinationPath, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(BootstrapperVersion.UserAgent);

        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"failed to download {url}: HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        await using var fs = File.Create(destinationPath);
        await response.Content.CopyToAsync(fs, ct).ConfigureAwait(false);
    }

    private static async Task EnsureOpenSslAsync(DistroInfo distro, PhaseLogger logger, CancellationToken ct)
    {
        if (await ProcessRunner.ExistsOnPathAsync("openssl", ct).ConfigureAwait(false))
        {
            return;
        }

        logger.Info("    installing openssl...");
        switch (distro.Family)
        {
            case DistroFamily.Debian:
                await RunAptInstallAsync(["openssl"], logger, ct).ConfigureAwait(false);
                break;
            case DistroFamily.RedHat:
                await RunDnfInstallAsync(["openssl"], logger, ct).ConfigureAwait(false);
                break;
        }
    }

    /// <summary>Shared install seam for the mDNS prompts; both callers get identical
    /// DEBIAN_FRONTEND/<c>-y</c> shape and error surface.</summary>
    internal static async Task RunInstallAsync(
        DistroInfo distro,
        IEnumerable<string> packages,
        PhaseLogger logger,
        CancellationToken ct)
    {
        switch (distro.Family)
        {
            case DistroFamily.Debian:
                await RunAptInstallAsync(packages, logger, ct).ConfigureAwait(false);
                break;
            case DistroFamily.RedHat:
                await RunDnfInstallAsync(packages, logger, ct).ConfigureAwait(false);
                break;
            default:
                throw new InvalidOperationException(
                    $"Cannot install packages on unsupported distro family {distro.Family}. " +
                    $"Manual install required. Distro: {distro.PrettyName ?? distro.Id}.");
        }
    }

    private static async Task RunAptInstallAsync(IEnumerable<string> packages, PhaseLogger logger, CancellationToken ct)
    {
        var env = new Dictionary<string, string?> { ["DEBIAN_FRONTEND"] = "noninteractive" };
        var update = await ProcessRunner.RunAsync("apt-get", ["update"], environment: env, ct: ct).ConfigureAwait(false);
        if (update.ExitCode != 0)
        {
            logger.Warn($"apt-get update exited {update.ExitCode}: {update.StdErr.Trim()}");
        }
        var install = await ProcessRunner.RunAsync("apt-get",
            ["install", "-y", .. packages], environment: env, ct: ct).ConfigureAwait(false);
        if (install.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"apt-get install failed (exit {install.ExitCode}):\n{install.StdErr.Trim()}");
        }
    }

    private static async Task RunDnfInstallAsync(IEnumerable<string> packages, PhaseLogger logger, CancellationToken ct)
    {
        var install = await ProcessRunner.RunAsync("dnf", ["install", "-y", .. packages], ct: ct).ConfigureAwait(false);
        if (install.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"dnf install failed (exit {install.ExitCode}):\n{install.StdErr.Trim()}");
        }
    }

    private static async Task EnsureAioLimitAsync(int scyllaNodes, PhaseLogger logger, CancellationToken ct)
    {
        if (!File.Exists(AioSysctlPath))
        {
            logger.Warn($"{AioSysctlPath} not present; skipping AIO tuning (likely running in a constrained container).");
            return;
        }

        // Cassandra-only (scyllaNodes==0) or an operator override still gets persisted so
        // reboots can't silently regress.
        var minRequired = scyllaNodes * AioPerNodeMin + AioHeadroom;
        var target = scyllaNodes * AioPerNodeRecommended + AioHeadroom;

        var current = int.Parse((await File.ReadAllTextAsync(AioSysctlPath, ct).ConfigureAwait(false)).Trim());
        if (current >= minRequired)
        {
            logger.Info($"    fs.aio-max-nr={current} (>= {minRequired} for {scyllaNodes} Scylla node(s)); ok");
            await PersistSysctlAsync(Math.Max(current, target), logger, ct).ConfigureAwait(false);
            return;
        }

        logger.Info($"    fs.aio-max-nr={current} (< {minRequired} for {scyllaNodes} Scylla node(s)); raising to {target}");
        try
        {
            await File.WriteAllTextAsync(AioSysctlPath, target.ToString(), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to raise fs.aio-max-nr to {target}: {ex.Message}. " +
                $"Set it manually with `sudo sysctl -w fs.aio-max-nr={target}` and re-run.", ex);
        }
        await PersistSysctlAsync(target, logger, ct).ConfigureAwait(false);
    }

    private static async Task PersistSysctlAsync(int value, PhaseLogger logger, CancellationToken ct)
    {
        var content = $"# Interfold bootstrapper - required by Scylla/Seastar.\nfs.aio-max-nr = {value}\n";
        try
        {
            await File.WriteAllTextAsync(SysctlDropIn, content, ct).ConfigureAwait(false);
            logger.Info($"    persisted to {SysctlDropIn}");
        }
        catch (Exception ex)
        {
            logger.Warn($"could not write {SysctlDropIn}: {ex.Message} (setting is in effect for this boot)");
        }
    }

    [SupportedOSPlatform("windows")]
    private static async Task RunWindowsAsync(BootstrapOptions options, PhaseLogger logger, CancellationToken ct)
    {
        logger.Info($"    detected host: {RuntimeInformation.OSDescription}");

        // Docker already on PATH → no elevation. Missing → Administrator for winget/choco.
        if (await DockerComposeReadyAsync(ct).ConfigureAwait(false))
        {
            logger.Info("    docker + compose plugin already present");
        }
        else
        {
            EnsureAdministrator(logger);
            await InstallDockerDesktopAsync(logger, ct).ConfigureAwait(false);
            RefreshProcessPathFromMachine();
            await EnsureDockerDesktopRunningAsync(logger, ct).ConfigureAwait(false);
        }

        var scyllaNodes = await PeekScyllaNodeCountAsync(options, logger, ct).ConfigureAwait(false);
        await ProbeContainerAioAsync(scyllaNodes, logger, ct).ConfigureAwait(false);
    }

    internal static async Task<bool> DockerComposeReadyAsync(CancellationToken ct = default)
    {
        if (!await ProcessRunner.ExistsOnPathAsync("docker", ct).ConfigureAwait(false))
        {
            return false;
        }

        try
        {
            var compose = await ProcessRunner.RunAsync("docker", ["compose", "version"], ct: ct).ConfigureAwait(false);
            return compose.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    internal static void EnsureAdministrator(PhaseLogger logger, Func<bool>? isAdministrator = null)
    {
        if ((isAdministrator ?? IsCurrentProcessElevated)())
        {
            return;
        }

        logger.PhaseFail(BootstrapPhase.Prereqs.ToWireName(), PhaseFailureReasons.NonAdmin);
        throw new InvalidOperationException(
            "Installing Docker Desktop requires an elevated process. Re-run from an " +
            "Administrator terminal, or install Docker Desktop first: " +
            "https://docs.docker.com/desktop/setup/install/windows-install/");
    }

    [SupportedOSPlatform("windows")]
    internal static bool IsCurrentProcessElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>Elevation only on the Docker Desktop install path (docker+compose missing).</summary>
    internal static bool NeedsAdministratorForDockerInstall(bool dockerComposeReady) =>
        !dockerComposeReady;

    private static async Task InstallDockerDesktopAsync(PhaseLogger logger, CancellationToken ct)
    {
        logger.Info("    docker not on PATH; installing Docker Desktop...");

        if (await ProcessRunner.ExistsOnPathAsync("winget", ct).ConfigureAwait(false))
        {
            logger.Info("    winget install Docker.DockerDesktop...");
            var winget = await ProcessRunner.RunAsync(
                "winget",
                ["install", "-e", "--id", "Docker.DockerDesktop",
                 "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity"],
                ct: ct).ConfigureAwait(false);
            if (winget.ExitCode == 0)
            {
                return;
            }
            logger.Warn($"winget install exited {winget.ExitCode}: {winget.StdErr.Trim()}");
        }

        if (await ProcessRunner.ExistsOnPathAsync("choco", ct).ConfigureAwait(false))
        {
            logger.Info("    choco install docker-desktop...");
            var choco = await ProcessRunner.RunAsync(
                "choco", ["install", "docker-desktop", "-y"], ct: ct).ConfigureAwait(false);
            if (choco.ExitCode == 0)
            {
                return;
            }
            logger.Warn($"choco install exited {choco.ExitCode}: {choco.StdErr.Trim()}");
        }

        throw new InvalidOperationException(
            "Could not install Docker Desktop (winget/choco missing or failed). " +
            "Install it from https://docs.docker.com/desktop/setup/install/windows-install/ " +
            "and re-run. A reboot, WSL2, or signing out to pick up the docker-users group may be required.");
    }

    private static void RefreshProcessPathFromMachine()
    {
        RefreshProcessPathFromMachine(
            target => Environment.GetEnvironmentVariable("PATH", target),
            value => Environment.SetEnvironmentVariable("PATH", value));
    }

    /// <summary>Merges Machine+User PATH into the process env (post-winget installs).</summary>
    internal static void RefreshProcessPathFromMachine(
        Func<EnvironmentVariableTarget, string?> getPath,
        Action<string> setProcessPath)
    {
        var machine = getPath(EnvironmentVariableTarget.Machine) ?? "";
        var user = getPath(EnvironmentVariableTarget.User) ?? "";
        var combined = string.IsNullOrEmpty(user) ? machine : machine + Path.PathSeparator + user;
        if (!string.IsNullOrEmpty(combined))
        {
            setProcessPath(combined);
        }
    }

    private static async Task EnsureDockerDesktopRunningAsync(PhaseLogger logger, CancellationToken ct)
    {
        const int TimeoutSeconds = 180;
        var deadline = DateTime.UtcNow.AddSeconds(TimeoutSeconds);

        TryStartDockerDesktop(logger);

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (await DockerComposeReadyAsync(ct).ConfigureAwait(false)
                && await DockerInfoOkAsync(ct).ConfigureAwait(false))
            {
                logger.Info("    docker daemon is reachable");
                return;
            }
            await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        }

        throw new InvalidOperationException(
            "Docker Desktop is installed but the daemon did not become reachable within " +
            $"{TimeoutSeconds}s. Start Docker Desktop, wait until it reports running, then re-run. " +
            "A reboot or signing out to pick up the docker-users group is sometimes required.");
    }

    private static void TryStartDockerDesktop(PhaseLogger logger)
    {
        var exe = HostPaths.FindDockerDesktopExecutable();
        if (exe is null)
        {
            logger.Warn("Docker Desktop.exe not found under Program Files or LocalAppData; waiting for docker on PATH anyway.");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = true,
            });
            logger.Info("    started Docker Desktop");
        }
        catch (Exception ex)
        {
            logger.Warn($"could not start Docker Desktop ({ex.GetType().Name}: {ex.Message}); waiting for docker anyway.");
        }
    }

    private static async Task<bool> DockerInfoOkAsync(CancellationToken ct)
    {
        try
        {
            var info = await ProcessRunner.RunAsync("docker", ["info"], ct: ct).ConfigureAwait(false);
            return info.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    internal static async Task ProbeContainerAioAsync(int scyllaNodes, PhaseLogger logger, CancellationToken ct)
    {
        var minRequired = MinimumContainerAio(scyllaNodes);
        try
        {
            var probe = await ProcessRunner.RunAsync(
                "docker",
                ["run", "--rm", "alpine", "cat", "/proc/sys/fs/aio-max-nr"],
                ct: ct).ConfigureAwait(false);
            if (probe.ExitCode != 0 || !int.TryParse(probe.StdOut.Trim(), out var current))
            {
                logger.Warn("could not probe fs.aio-max-nr inside a container; skipping AIO check. " +
                            "Scylla/Seastar needs a high aio-max-nr in the Docker Desktop Linux VM.");
                return;
            }

            if (IsContainerAioSufficient(current, scyllaNodes))
            {
                logger.Info($"    container fs.aio-max-nr={current} (>= {minRequired} for {scyllaNodes} Scylla node(s)); ok");
                return;
            }

            logger.Warn(
                $"container fs.aio-max-nr={current} (< {minRequired} for {scyllaNodes} Scylla node(s)). " +
                "Raise it inside the Docker Desktop Linux VM if Scylla fails to start; the bootstrapper " +
                "does not write WSL sysctl (distro name is not stable).");
        }
        catch (Exception ex)
        {
            logger.Warn($"AIO container probe failed ({ex.GetType().Name}: {ex.Message}); continuing.");
        }
    }

    internal static int MinimumContainerAio(int scyllaNodes) =>
        scyllaNodes * AioPerNodeMin + AioHeadroom;

    internal static bool IsContainerAioSufficient(int current, int scyllaNodes) =>
        current >= MinimumContainerAio(scyllaNodes);
}
