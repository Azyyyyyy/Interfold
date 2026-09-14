using System.Reflection;

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
}
