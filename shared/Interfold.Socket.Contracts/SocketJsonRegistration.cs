using System.Runtime.CompilerServices;
using Interfold.Shared.Contracts;

namespace Interfold.Socket.Contracts;

// unit tests and PhxFrame serialize before API DI registers resolvers.
internal static class SocketJsonRegistration
{
#pragma warning disable CA2255
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Register()
    {
        SocketJson.AddTypeInfoResolver(SharedContractsJsonContext.Default);
        SocketJson.AddTypeInfoResolver(SocketJsonContext.Default);
    }
}
