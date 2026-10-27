using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Interfold.Shared.Contracts;

namespace Interfold.Shared.Api.Json;

public static class InterfoldJson
{
    public static void ApplyShared(JsonSerializerOptions options)
    {
        options.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
        if (options.Converters.All(static c => c is not UtcDateTimeConverter))
        {
            options.Converters.Add(new UtcDateTimeConverter());
            options.Converters.Add(new UtcDateTimeOffsetConverter());
        }

        InsertResolver(options, SharedApiJsonContext.Default);
        InsertResolver(options, SharedContractsJsonContext.Default);
    }

    public static IServiceCollection AddInterfoldJsonContext(
        this IServiceCollection services,
        IJsonTypeInfoResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(
            options => InsertResolver(options.SerializerOptions, resolver));
        return services;
    }

    public static void InsertResolver(JsonSerializerOptions options, IJsonTypeInfoResolver resolver)
    {
        if (!options.TypeInfoResolverChain.Contains(resolver))
            options.TypeInfoResolverChain.Insert(0, resolver);
    }
}