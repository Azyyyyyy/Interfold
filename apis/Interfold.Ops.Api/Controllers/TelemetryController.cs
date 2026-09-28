using System.Text.Json.Serialization;
using Interfold.Shared.Api.Controllers.Base;
using Interfold.Shared.Contracts.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Interfold.Ops.Api.Controllers;

/// <summary>Client OTLP discovery — advertises a collector URL; the API never ingests OTLP.</summary>
[Route("api/telemetry")]
public sealed class TelemetryController : InterfoldControllerBase
{
    private readonly IOptionsMonitor<ObservabilityConfiguration> _observability;

    public TelemetryController(IOptionsMonitor<ObservabilityConfiguration> observability)
    {
        _observability = observability;
    }

    /// <summary>
    /// Returns the OTLP/HTTP collector base URL for client span/log export.
    /// Prefers <see cref="ObservabilityConfiguration.ClientOtlpHttpEndpoint"/>; else
    /// <see cref="ObservabilityConfiguration.OtlpEndpoint"/> only when
    /// <see cref="ObservabilityConfiguration.AdvertiseOtlpToClients"/> is true.
    /// Bare JSON (not the <c>{ data }</c> envelope). Unavailable → 404.
    /// </summary>
    /// <remarks>Anonymous so wasm/mobile can discover the collector before a session exists.</remarks>
    [AllowAnonymous]
    [HttpGet("otlp")]
    public IActionResult GetOtlpDiscovery()
    {
        var opts = _observability.CurrentValue;
        var endpoint = ResolveDiscoveryEndpoint(opts);
        if (endpoint is null)
            return NotFound();

        return Ok(new OtlpDiscoveryResponse(endpoint));
    }

    public static string? ResolveDiscoveryEndpoint(ObservabilityConfiguration opts)
    {
        var client = opts.ClientOtlpHttpEndpoint?.Trim();
        if (!string.IsNullOrEmpty(client))
            return client;

        if (!opts.AdvertiseOtlpToClients)
            return null;

        var server = opts.OtlpEndpoint?.Trim();
        return string.IsNullOrEmpty(server) ? null : server;
    }
}

/// <summary>Wire body for <c>GET /api/telemetry/otlp</c>. CamelCase field name is required
/// by the client contract (global MVC policy is snake_case).</summary>
public sealed record OtlpDiscoveryResponse(
    [property: JsonPropertyName("otlpHttpEndpoint")] string OtlpHttpEndpoint);
