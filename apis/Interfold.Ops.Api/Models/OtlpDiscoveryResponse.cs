using System.Text.Json.Serialization;

namespace Interfold.Ops.Api.Models;

/// <summary>Wire body for <c>GET /api/telemetry/otlp</c>. CamelCase field name is required
/// by the client contract (global JSON policy is snake_case).</summary>
public sealed record OtlpDiscoveryResponse(
    [property: JsonPropertyName("otlpHttpEndpoint")] string OtlpHttpEndpoint);
