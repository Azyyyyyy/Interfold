namespace Interfold.Shared.Contracts.Configuration;

using Interfold.Shared.Contracts.Configuration.Validation;

/// <summary>
/// OpenTelemetry and observability configuration for traces and metrics export.
/// Binds from environment variables with OCTOCON_ prefix.
/// </summary>
public sealed class ObservabilityConfiguration
{
    public const string SectionName = "Octocon:Observability";

    /// <summary>
    /// OpenTelemetry Protocol (OTLP) endpoint for the API's own trace and metrics export.
    /// When set, enables export; when null/empty, metrics remain in-process only.
    /// Example: 'http://localhost:4317'
    /// Env: OCTOCON_OTLP_ENDPOINT
    /// The <see cref="AbsoluteHttpUriAttribute"/> mirrors the bootstrapper's
    /// <c>config.observability.otlpEndpoint</c> rule: null/empty is fine (disables
    /// export); non-empty must parse as an absolute http(s) URL.
    /// </summary>
    [AbsoluteHttpUri]
    public string? OtlpEndpoint { get; set; }

    /// <summary>
    /// When true, <c>GET /api/telemetry/otlp</c> may advertise <see cref="OtlpEndpoint"/>
    /// if <see cref="ClientOtlpHttpEndpoint"/> is unset. Off by default so setting the
    /// API exporter URL does not expose it to clients unless the operator opts in.
    /// Env: OCTOCON_ADVERTISE_OTLP_TO_CLIENTS
    /// </summary>
    public bool AdvertiseOtlpToClients { get; set; }

    /// <summary>
    /// Optional dedicated OTLP/HTTP URL for <c>GET /api/telemetry/otlp</c>. When set,
    /// it wins over <see cref="OtlpEndpoint"/> regardless of
    /// <see cref="AdvertiseOtlpToClients"/>. Use when clients need a different URL than
    /// the API exporter (e.g. public :4318 vs internal :4317).
    /// Env: OCTOCON_CLIENT_OTLP_HTTP_ENDPOINT
    /// </summary>
    [AbsoluteHttpUri]
    public string? ClientOtlpHttpEndpoint { get; set; }
}
