namespace Interfold.Bootstrapper.Util;

/// <summary>
/// Host-OS path helpers shared by install-service (binary name) and ConfigPhase
/// (output-dir equality). Linux is case-sensitive; Windows is not.
/// </summary>
internal static class HostPaths
{
    public const string UnixFileName = "interfold-bootstrap";
    public const string WindowsFileName = "interfold-bootstrap.exe";

    public static string BootstrapperFileName =>
        OperatingSystem.IsWindows() ? WindowsFileName : UnixFileName;

    public static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static bool OutputDirsEqual(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), PathComparison);

    public static string ResolveBootstrapperBinary(string? binaryPathOverride)
    {
        if (!string.IsNullOrWhiteSpace(binaryPathOverride))
        {
            return Path.GetFullPath(binaryPathOverride);
        }
        return Path.Combine(AppContext.BaseDirectory, BootstrapperFileName);
    }

    /// <summary>Program Files (machine-wide) and LocalAppData (winget per-user).</summary>
    public static IReadOnlyList<string> DockerDesktopExecutableCandidates() =>
    [
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Docker", "Docker", "Docker Desktop.exe"),
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "DockerDesktop", "Docker Desktop.exe"),
    ];

    public static string? FindDockerDesktopExecutable()
    {
        foreach (var candidate in DockerDesktopExecutableCandidates())
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
