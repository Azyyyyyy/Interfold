using System.Runtime.CompilerServices;

namespace Interfold.Bootstrapper.Configuration;

file static class BootstrapJsonContextWarmup
{
    // Source-generated JsonSerializerContext metadata is not safe under concurrent first
    // access; TUnit parallelises many bootstrapper JSON tests and CI Linux hit AV during
    // Create_EdgeSection when Default.BootstrapConfig raced Configure().
    [ModuleInitializer]
    internal static void EnsureConfigured()
    {
        _ = BootstrapJsonContext.Default.BootstrapConfig;
        _ = BootstrapJsonContext.Default.BootstrapDatastoresPeek;
        _ = BootstrapJsonContext.Default.GeneratedSecrets;
        _ = BootstrapJsonContext.Default.CloudflareTunnelState;
        _ = BootstrapJsonContext.Default.CloudflareAccessState;
    }
}
