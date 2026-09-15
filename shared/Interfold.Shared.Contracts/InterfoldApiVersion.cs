using System.Reflection;

namespace Interfold.Shared.Contracts;

/// <summary>
/// Product SemVer stamped into <c>Interfold.Api.Host</c> at image build
/// (<c>InformationalVersion</c>). Independent of bootstrapper version and of
/// <see cref="InterfoldContractVersions"/> (wire freeze label).
/// </summary>
public static class InterfoldApiVersion
{
    public const string DefaultDevVersion = "0.0.0-dev";

    /// <summary>
    /// Entry-assembly informational version (CI stamp), or
    /// <see cref="DefaultDevVersion"/> for unstamped local builds.
    /// </summary>
    public static string InformationalVersion
    {
        get
        {
            var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
            return assembly
                       .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                       ?.InformationalVersion
                   ?? assembly.GetName().Version?.ToString()
                   ?? DefaultDevVersion;
        }
    }
}
