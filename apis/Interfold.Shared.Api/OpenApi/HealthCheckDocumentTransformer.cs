using Interfold.Shared.Contracts.Configuration;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Interfold.Shared.Api.OpenApi;

/// <summary>Adds the liveness / readiness / startup probes that live outside
/// feature-module route groups.</summary>
public sealed class HealthCheckDocumentTransformer : IOpenApiDocumentTransformer
{
    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        document.Paths ??= [];

        var healthy = new OpenApiResponse { Description = "Healthy" };
        var unhealthy = new OpenApiResponse { Description = "Unhealthy" };

        document.Paths[HealthEndpoints.Live] = PathItem("Liveness probe",
            "Returns 200 if the process is running. No dependency checks.",
            ("200", healthy));

        document.Paths[HealthEndpoints.Ready] = PathItem("Readiness probe",
            "Returns 200 if all dependencies (SQLite) are reachable.",
            ("200", healthy),
            ("503", unhealthy));

        document.Paths[HealthEndpoints.Startup] = PathItem("Startup probe",
            "Returns 200 once all dependencies are initialized. Uses longer timeouts than readiness.",
            ("200", healthy),
            ("503", unhealthy));

        return Task.CompletedTask;
    }

    private static OpenApiPathItem PathItem(string summary, string description, params (string Status, OpenApiResponse Response)[] responses)
    {
        var operation = new OpenApiOperation
        {
            Tags = new HashSet<OpenApiTagReference> { new("Health") },
            Summary = summary,
            Description = description,
            Responses = new OpenApiResponses(),
        };

        foreach (var (status, response) in responses)
            operation.Responses[status] = response;

        return new OpenApiPathItem
        {
            Operations = new Dictionary<HttpMethod, OpenApiOperation>
            {
                [HttpMethod.Get] = operation,
            },
        };
    }
}
