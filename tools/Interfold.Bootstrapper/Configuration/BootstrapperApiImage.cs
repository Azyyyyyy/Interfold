using Interfold.Shared.Contracts.Configuration;

namespace Interfold.Bootstrapper.Configuration;

internal static class BootstrapperApiImage
{
    internal const string BleedingEdge = "ghcr.io/azyyyyyy/interfold-api:bleeding-edge";

    // Debug pins develop so a local bootstrapper runs the API that includes SQLite.
    // Release pins the stable image.
#if DEBUG
    internal const string Default = BleedingEdge;
#else
    internal const string Default = DefaultContainerImages.Api;
#endif
}
