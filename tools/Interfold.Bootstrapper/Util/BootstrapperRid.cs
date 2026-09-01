using System.Runtime.InteropServices;

namespace Interfold.Bootstrapper.Util;

internal static class BootstrapperRid
{
    internal static string DetectLinuxRid()
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException(
                "Bootstrapper self-update is only supported on Linux hosts.");
        }

        return RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "linux-x64",
            Architecture.Arm64 => "linux-arm64",
            Architecture.Arm => "linux-arm",
            _ => throw new PlatformNotSupportedException(
                $"Unsupported Linux architecture for bootstrapper self-update: {RuntimeInformation.OSArchitecture}."),
        };
    }

    internal static string TarballAssetName(string rid) => $"interfold-bootstrap-{rid}.tar.gz";
}
