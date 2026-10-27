using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Interfold.Shared.Api.OpenApi;

/// <summary>Documents both JSON and multipart bodies on the avatar PUT routes.</summary>
public sealed class MultipleContentTypeOperationTransformer : IOpenApiOperationTransformer
{
    private static readonly string[] AllowedConflicts =
    [
        "api/settings/avatar",
        "api/systems/me/alters/{*}/avatar",
    ];

    public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        var route = context.Description.RelativePath;
        var normalised = route is null
            ? null
            : System.Text.RegularExpressions.Regex.Replace(route.ToLowerInvariant(), @"\{[^/{}]+\}", "{*}");

        if (AllowedConflicts.All(allowed => normalised?.Contains(allowed) != true))
            return Task.CompletedTask;

        if (!string.Equals(context.Description.HttpMethod, "PUT", StringComparison.OrdinalIgnoreCase))
            return Task.CompletedTask;

        var content = new Dictionary<string, OpenApiMediaType>(
            operation.RequestBody?.Content ?? new Dictionary<string, OpenApiMediaType>());

        if (!content.ContainsKey("application/json"))
        {
            content["application/json"] = new OpenApiMediaType
            {
                Schema = new OpenApiSchema { Type = JsonSchemaType.Object },
            };
        }

        if (!content.ContainsKey("multipart/form-data"))
        {
            content["multipart/form-data"] = new OpenApiMediaType
            {
                Schema = new OpenApiSchema
                {
                    Type = JsonSchemaType.Object,
                    Properties = new Dictionary<string, IOpenApiSchema>
                    {
                        ["file"] = new OpenApiSchema
                        {
                            Type = JsonSchemaType.String,
                            Format = "binary",
                            Description = "The avatar file to upload",
                        },
                        ["idempotencyKey"] = new OpenApiSchema
                        {
                            Type = JsonSchemaType.String,
                        },
                    },
                },
            };
        }

        operation.RequestBody = new OpenApiRequestBody { Content = content };

        operation.Description = string.IsNullOrEmpty(operation.Description)
            ? "Accepts both JSON (with avatarUrl) and multipart/form-data (with file upload)."
            : operation.Description + "\n\nThis endpoint accepts both JSON (with avatarUrl) and multipart/form-data (with file upload).";

        return Task.CompletedTask;
    }
}
