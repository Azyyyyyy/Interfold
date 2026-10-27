using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Interfold.Shared.Contracts;

namespace Interfold.Socket.Contracts;

public static class SocketJson
{
    private static readonly Lock Gate = new();

    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            Converters =
            {
                new UtcDateTimeConverter(),
                new UtcDateTimeOffsetConverter()
            }
        };
        options.TypeInfoResolverChain.Add(SharedContractsJsonContext.Default);
        options.TypeInfoResolverChain.Add(SocketJsonContext.Default);
        return options;
    }

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

