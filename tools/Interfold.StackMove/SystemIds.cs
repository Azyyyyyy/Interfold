using Interfold.Shared.Contracts.Ids;

namespace Interfold.StackMove;

internal static class SystemIds
{
    public static string Bare(string id) => ScopedSystemId.StripRegionPrefix(id);
}
