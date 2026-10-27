using Microsoft.AspNetCore.OpenApi;

namespace Interfold.Shared.Api.OpenApi;

public static class OpenApiServiceCollectionExtensions
{
    public static IServiceCollection AddInterfoldOpenApi(this IServiceCollection services)
    {
        services.AddOpenApi("v1", options =>
        {
            options.AddDocumentTransformer<InterfoldOpenApiDocumentTransformer>();
            options.AddDocumentTransformer<HealthCheckDocumentTransformer>();
            options.AddOperationTransformer<MultipleContentTypeOperationTransformer>();
        });
        return services;
    }
}
