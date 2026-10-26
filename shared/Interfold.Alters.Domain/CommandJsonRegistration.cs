using System.Runtime.CompilerServices;
using Interfold.Alters.Contracts;
using Interfold.Shared.Domain;

namespace Interfold.Alters.Domain;

// Handlers serialize before API DI runs; module init is the AOT-safe registration point.
internal static class CommandJsonRegistration
{
#pragma warning disable CA2255
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Register()
        => CommandSerialization.AddTypeInfoResolver(AltersCommandJsonContext.Default);
}
