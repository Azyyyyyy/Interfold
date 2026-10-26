using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Interfold.Shared.Domain;

public static class CommandSerialization
{
    private static readonly Lock Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Registers a source-gen context for idempotency payload/result types.
    /// CamelCase must stay — stored payload hashes are of that shape.</summary>
    public static void AddTypeInfoResolver(IJsonTypeInfoResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        lock (Gate)
        {
            if (JsonOptions.TypeInfoResolverChain.Contains(resolver) || JsonOptions.IsReadOnly)
                return;

            JsonOptions.TypeInfoResolverChain.Add(resolver);
        }
    }

    public static string Serialize<T>(T value)
        => JsonSerializer.Serialize(value, (JsonTypeInfo<T>)JsonOptions.GetTypeInfo(typeof(T)));

    public static T? Deserialize<T>(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return default;
        }

        return JsonSerializer.Deserialize(value, (JsonTypeInfo<T>)JsonOptions.GetTypeInfo(typeof(T)));
    }

    public static string Hash(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var hashBytes = SHA256.HashData(bytes);
        return Convert.ToHexString(hashBytes);
    }
}