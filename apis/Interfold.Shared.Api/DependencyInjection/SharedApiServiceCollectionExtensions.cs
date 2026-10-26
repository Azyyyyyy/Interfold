using Interfold.Shared.Api.Json;
using Interfold.Shared.Api.Services;

namespace Interfold.Shared.Api.DependencyInjection;

/// <summary>Shared-spine DI wiring consumed by <c>Program.cs</c> and every
/// <c>Interfold.&lt;Feature&gt;.Api</c> module.</summary>
public static class SharedApiServiceCollectionExtensions
{
    /// <summary>Registers the shared-spine services: HTTP JSON options and the
    /// <see cref="ExceptionHandler"/>. Does <b>not</b> map routes — each feature
    /// module owns <c>MapXxxModule</c>.</summary>
    public static IServiceCollection AddSharedApi(this IServiceCollection services)
    {
        services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(
            options => InterfoldJson.ApplyShared(options.SerializerOptions));

        services.AddExceptionHandler<ExceptionHandler>();

        return services;
    }
}
