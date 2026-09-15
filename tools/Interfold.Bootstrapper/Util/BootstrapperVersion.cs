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

        var localFull = NormalizeIdentity(InformationalVersion);
        var remoteFull = NormalizeIdentity(remoteVersion);
        if (string.Equals(remoteFull, localFull, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var localCore = StripBuildMetadata(InformationalVersion);
        var remoteCore = StripBuildMetadata(remoteVersion);

        if (TryParseComparable(localCore, out var localVer)
            && TryParseComparable(remoteCore, out var remoteVer))
        {
            if (remoteVer != localVer)
            {
                return remoteVer > localVer;
            }

            // Same SemVer core: rolling stamps (`1.2.3+abc` vs `1.2.3+def`) update when
            // both sides carry build metadata. One-sided metadata (SDK SourceRevisionId
            // vs a clean remote, or pin tag without +meta) is not an update.
            var localMeta = GetBuildMetadata(InformationalVersion);
            var remoteMeta = GetBuildMetadata(remoteVersion);
            return localMeta.Length > 0
                   && remoteMeta.Length > 0
                   && !string.Equals(localMeta, remoteMeta, StringComparison.OrdinalIgnoreCase);
        }

        return !string.Equals(remoteCore, localCore, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeIdentity(string version)
        => StripLeadingV(version.Trim());

    private static string GetBuildMetadata(string version)
    {
        var trimmed = version.Trim();
        var plus = trimmed.IndexOf('+');
        return plus >= 0 ? trimmed[(plus + 1)..].Trim() : string.Empty;
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
        // Allow pre-release-ish local stamps like 0.0.0-dev by taking the numeric prefix.
        var dash = core.IndexOf('-');
        if (dash > 0)
        {
            core = core[..dash];
        }

        if (Version.TryParse(core, out var v))
        {
            parsed = v;
            return true;
        }

        return false;
    }
}
