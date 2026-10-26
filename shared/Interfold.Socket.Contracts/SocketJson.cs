using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Interfold.Shared.Contracts;

namespace Interfold.Socket.Contracts;

public static class SocketJson
{
    private static readonly Lock Gate = new();

    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters =
        {
            new UtcDateTimeConverter(),
            new UtcDateTimeOffsetConverter()
        }
    };

    public static void AddTypeInfoResolver(IJsonTypeInfoResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        lock (Gate)
        {
            if (Options.TypeInfoResolverChain.Contains(resolver) || Options.IsReadOnly)
                return;

            Options.TypeInfoResolverChain.Add(resolver);
        }
    }
}

