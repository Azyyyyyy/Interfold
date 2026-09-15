using System.Reflection;
using System.Runtime.InteropServices;

namespace Interfold.Bootstrapper.Util;

internal static class BootstrapperVersion
{
    internal const string DefaultDevVersion = "0.0.0-dev";

    internal static string InformationalVersion =>
        Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
        ?? DefaultDevVersion;

    internal static string UserAgent => $"interfold-bootstrap/{InformationalVersion}";

    internal static string RuntimeDescription
    {
        get
        {
            var rid = BootstrapperRid.TryDetectHostRid(out var detected)
                ? detected
                : "unsupported";
            var arch = RuntimeInformation.OSArchitecture switch
            {
                Architecture.X64 => "x64",
                Architecture.Arm64 => "arm64",
                Architecture.Arm => "arm",
                _ => RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
            };
            return $"{RuntimeInformation.OSDescription} ({rid}; {arch})";
        }
    }

    internal static bool IsUpdateAvailable(string remoteVersion, bool force)
    {
        if (force)
        {
            return true;
        }

        var local = StripBuildMetadata(InformationalVersion);
        var remote = StripBuildMetadata(remoteVersion);

        if (string.Equals(remote, local, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (TryParseComparable(local, out var localVer)
            && TryParseComparable(remote, out var remoteVer))
        {
            return remoteVer > localVer;
        }

        return !string.Equals(remote, local, StringComparison.OrdinalIgnoreCase);
    }

    private static string StripBuildMetadata(string version)
    {
        var trimmed = version.Trim();
        var plus = trimmed.IndexOf('+');
        var withoutMeta = plus >= 0 ? trimmed[..plus].Trim() : trimmed;
        return StripLeadingV(withoutMeta);
    }

    private static string StripLeadingV(string version)
        => version.Length >= 2
           && (version[0] is 'v' or 'V')
           && char.IsAsciiDigit(version[1])
            ? version[1..]
            : version;

    private static bool TryParseComparable(string version, out Version parsed)
    {
        parsed = default!;
        var core = StripLeadingV(version.Split('+', 2)[0].Trim());
        if (Version.TryParse(core, out var v))
        {
            parsed = v;
            return true;
        }

        return false;
    }
}
