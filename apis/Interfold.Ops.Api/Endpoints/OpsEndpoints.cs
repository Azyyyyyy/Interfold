using System.Security.Cryptography.X509Certificates;
using Interfold.Ops.Api.Models;
using Interfold.Shared.Contracts.Configuration;
using Microsoft.Extensions.Options;

namespace Interfold.Ops.Api.DependencyInjection;

public static partial class OpsModuleServiceCollectionExtensions
{
    public static IEndpointRouteBuilder MapOpsModule(this IEndpointRouteBuilder endpoints)
    {
        var trust = endpoints.MapGroup("/.well-known").AllowAnonymous();
        trust.MapMethods("/interfold-root-ca.crt", [HttpMethods.Get, HttpMethods.Head], GetRootCaDer);
        trust.MapMethods("/interfold-root-ca.pem", [HttpMethods.Get, HttpMethods.Head], GetRootCaPem);
        trust.MapMethods("/interfold-root-ca.sha256", [HttpMethods.Get, HttpMethods.Head], GetRootCaFingerprint);

        var telemetry = endpoints.MapGroup("/api/telemetry");
        telemetry.MapGet("/otlp", GetOtlpDiscovery).AllowAnonymous();
        return endpoints;
    }

    /// <summary>
    /// Returns the root CA in DER (binary) form. Negotiates the legacy
    /// <c>application/x-x509-ca-cert</c> MIME when requested via Accept.
    /// </summary>
    private static async Task<IResult> GetRootCaDer(HttpContext http, IOptions<TrustOptions> options, CancellationToken ct)
    {
        var opts = options.Value;
        if (string.IsNullOrWhiteSpace(opts.RootCaPath) || !File.Exists(opts.RootCaPath))
            return TypedResults.NotFound();

        using var cert = X509CertificateLoader.LoadCertificateFromFile(opts.RootCaPath);
        var der = cert.RawData;

        await ApplyTrustCacheHeadersAsync(http, opts, ct);
        var contentType = AcceptsLegacyCaCertMime(http) ? "application/x-x509-ca-cert" : "application/pkix-cert";
        return TypedResults.File(der, contentType, "interfold-root-ca.crt");
    }

    /// <summary>Returns the root CA in PEM form.</summary>
    private static async Task<IResult> GetRootCaPem(HttpContext http, IOptions<TrustOptions> options, CancellationToken ct)
    {
        var opts = options.Value;
        if (string.IsNullOrWhiteSpace(opts.RootCaPath) || !File.Exists(opts.RootCaPath))
            return TypedResults.NotFound();

        var bytes = await File.ReadAllBytesAsync(opts.RootCaPath, ct);
        await ApplyTrustCacheHeadersAsync(http, opts, ct);
        return TypedResults.File(bytes, "application/x-pem-file", "interfold-root-ca.pem");
    }

    /// <summary>Returns the SHA-256 fingerprint of the root CA as uppercase colon-hex.</summary>
    private static async Task<IResult> GetRootCaFingerprint(HttpContext http, IOptions<TrustOptions> options, CancellationToken ct)
    {
        var opts = options.Value;
        if (string.IsNullOrWhiteSpace(opts.RootCaFingerprintPath) || !File.Exists(opts.RootCaFingerprintPath))
            return TypedResults.NotFound();

        var bytes = await File.ReadAllBytesAsync(opts.RootCaFingerprintPath, ct);
        await ApplyTrustCacheHeadersAsync(http, opts, ct);
        return TypedResults.File(bytes, "text/plain; charset=utf-8", "interfold-root-ca.sha256");
    }

    private static async Task ApplyTrustCacheHeadersAsync(HttpContext http, TrustOptions options, CancellationToken ct)
    {
        http.Response.Headers["Cache-Control"] = "public, max-age=60, must-revalidate";

        if (string.IsNullOrWhiteSpace(options.RootCaFingerprintPath) || !File.Exists(options.RootCaFingerprintPath))
            return;

        var fp = (await File.ReadAllTextAsync(options.RootCaFingerprintPath, ct)).Trim();
        if (fp.Length > 0)
            http.Response.Headers.ETag = $"\"{fp}\"";
    }

    private static bool AcceptsLegacyCaCertMime(HttpContext http)
    {
        foreach (var accept in http.Request.Headers.Accept)
        {
            if (string.IsNullOrEmpty(accept))
                continue;

            if (accept.Contains("application/x-x509-ca-cert", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>Returns the OTLP/HTTP collector base URL for client span/log export. Bare JSON (not the <c>{ data }</c> envelope). Unavailable → 404.</summary>
    /// <remarks>Anonymous so wasm/mobile can discover the collector before a session exists.</remarks>
    private static IResult GetOtlpDiscovery(IOptionsMonitor<ObservabilityConfiguration> observability)
    {
        var endpoint = ResolveDiscoveryEndpoint(observability.CurrentValue);
        return endpoint is null ? TypedResults.NotFound() : TypedResults.Ok(new OtlpDiscoveryResponse(endpoint));
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
