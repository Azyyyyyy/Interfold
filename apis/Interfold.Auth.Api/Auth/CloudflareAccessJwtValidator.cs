using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using Interfold.Auth.Contracts.Configuration;
using Interfold.Shared.Contracts;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Interfold.Auth.Api.Auth;

/// <summary>Validates <c>Cf-Access-Jwt-Assertion</c> against the team JWKS. A valid token is
/// already allowlisted at Access — this only checks signature, iss, aud, and email.</summary>
public sealed class CloudflareAccessJwtValidator
{
    internal const string JwksUrlOverrideEnv = "INTERFOLD_CF_ACCESS_JWKS_URL";

    private readonly IOptionsMonitor<CloudflareAccessConfiguration> _options;
    private readonly HttpClient _http;
    private readonly object _gate = new();
    private JsonWebKeySet? _cachedKeys;
    private DateTimeOffset _keysExpireAt;

    public CloudflareAccessJwtValidator(
        IOptionsMonitor<CloudflareAccessConfiguration> options,
        HttpClient http)
    {
        _options = options;
        _http = http;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Interfold.Auth/cloudflare-access");
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public bool IsEnabled => _options.CurrentValue.IsEnabled;

    public async Task<CloudflareAccessJwtResult> ValidateAsync(
        string? assertion,
        CancellationToken ct)
    {
        var cfg = _options.CurrentValue;
        if (!cfg.IsEnabled)
            return CloudflareAccessJwtResult.Fail(ErrorCodes.CloudflareAccessUnavailable, "Cloudflare Access exchange is not configured.");

        if (string.IsNullOrWhiteSpace(assertion))
            return CloudflareAccessJwtResult.Fail(ErrorCodes.MissingAccessJwt, "Missing Cf-Access-Jwt-Assertion header.");

        JsonWebKeySet keys;
        try
        {
            keys = await GetKeysAsync(cfg.TeamDomain, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return CloudflareAccessJwtResult.Fail(ErrorCodes.InvalidToken, "Unable to fetch Cloudflare Access JWKS.");
        }

        var teamHost = NormalizeTeamHost(cfg.TeamDomain);
        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = $"https://{teamHost}",
            ValidateAudience = true,
            ValidAudience = cfg.Audience.Trim(),
            ValidateLifetime = true,
            RequireExpirationTime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = keys.GetSigningKeys(),
            ClockSkew = TimeSpan.FromMinutes(2),
        };

        try
        {
            var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
            _ = handler.ValidateToken(assertion, parameters, out var validated);
            if (validated is not JwtSecurityToken jwt)
                return CloudflareAccessJwtResult.Fail(ErrorCodes.InvalidToken, "Access token is not a JWT.");

            var email = jwt.Claims.FirstOrDefault(c => c.Type == "email")?.Value;
            var commonName = jwt.Claims.FirstOrDefault(c => c.Type == "common_name")?.Value;
            if (string.IsNullOrWhiteSpace(email) || !string.IsNullOrWhiteSpace(commonName))
            {
                return CloudflareAccessJwtResult.Fail(
                    ErrorCodes.InvalidToken,
                    "Access token is not a user identity (service tokens cannot be exchanged).");
            }

            return CloudflareAccessJwtResult.Ok(email.Trim());
        }
        catch (SecurityTokenException)
        {
            return CloudflareAccessJwtResult.Fail(ErrorCodes.InvalidToken, "Access JWT failed validation.");
        }
    }

    internal static string BuildJwksUrl(string teamDomain)
    {
        var overrideUrl = Environment.GetEnvironmentVariable(JwksUrlOverrideEnv);
        if (!string.IsNullOrWhiteSpace(overrideUrl))
            return overrideUrl.Trim();

        return $"https://{NormalizeTeamHost(teamDomain)}/cdn-cgi/access/certs";
    }

    internal static string NormalizeTeamHost(string teamDomain)
    {
        var raw = teamDomain.Trim();
        if (raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            raw = raw["https://".Length..];
        else if (raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            raw = raw["http://".Length..];
        return raw.TrimEnd('/');
    }

    private async Task<JsonWebKeySet> GetKeysAsync(string teamDomain, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_cachedKeys is not null && DateTimeOffset.UtcNow < _keysExpireAt)
                return _cachedKeys;
        }

        using var response = await _http.GetAsync(BuildJwksUrl(teamDomain), ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var keys = new JsonWebKeySet(json);
        if (keys.Keys.Count == 0)
            throw new InvalidOperationException("Cloudflare Access JWKS contained no keys.");

        lock (_gate)
        {
            _cachedKeys = keys;
            _keysExpireAt = DateTimeOffset.UtcNow.AddMinutes(10);
        }

        return keys;
    }
}

public readonly record struct CloudflareAccessJwtResult
{
    public bool Succeeded { get; init; }
    public string? Email { get; init; }
    public ErrorCode? Error { get; init; }
    public string? Message { get; init; }

    public static CloudflareAccessJwtResult Ok(string email)
        => new() { Succeeded = true, Email = email };

    public static CloudflareAccessJwtResult Fail(ErrorCode error, string message)
        => new() { Succeeded = false, Error = error, Message = message };
}
