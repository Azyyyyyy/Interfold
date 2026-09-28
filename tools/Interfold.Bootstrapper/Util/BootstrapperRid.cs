using System.Runtime.InteropServices;

namespace Interfold.Bootstrapper.Util;

internal static class BootstrapperRid
{
    internal static bool TryDetectHostRid(out string rid)
    {
        rid = "";
        if (OperatingSystem.IsLinux())
        {
            rid = RuntimeInformation.OSArchitecture switch
            {
                Architecture.X64 => "linux-x64",
                Architecture.Arm64 => "linux-arm64",
                Architecture.Arm => "linux-arm",
                _ => "",
            };
            return rid.Length > 0;
        }

        if (OperatingSystem.IsWindows())
        {
            // .NET has no win-arm RID; only x64 / arm64 (and win-x86, unused here).
            rid = RuntimeInformation.OSArchitecture switch
            {
                Architecture.X64 => "win-x64",
                Architecture.Arm64 => "win-arm64",
                _ => "",
            };
            return rid.Length > 0;
        }

        return false;
    }

    internal static string DetectHostRid()
    {
        if (TryDetectHostRid(out var rid))
        {
            return rid;
        }

        throw new PlatformNotSupportedException(
            $"Bootstrapper self-update is not supported on this OS/architecture " +
            $"({RuntimeInformation.OSDescription}; {RuntimeInformation.OSArchitecture}).");
    }

    /// <summary>Legacy name kept for call sites that assume Linux-only probes.</summary>
    internal static string DetectLinuxRid()
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException(
                "Bootstrapper Linux RID detection requires a Linux host.");
        }

        return DetectHostRid();
    }

    internal static string ReleaseAssetName(string rid) =>
        rid.StartsWith("win-", StringComparison.Ordinal)
            ? $"interfold-bootstrap-{rid}.zip"
            : $"interfold-bootstrap-{rid}.tar.gz";

    /// <summary>Backward-compatible alias for Linux tarball naming.</summary>
    internal static string TarballAssetName(string rid) => ReleaseAssetName(rid);

    internal static bool IsZipAsset(string rid) =>
        rid.StartsWith("win-", StringComparison.Ordinal);
}
