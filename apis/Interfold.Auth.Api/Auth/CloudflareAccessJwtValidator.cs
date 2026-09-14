using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Text.Json;
using Interfold.Auth.Contracts.Configuration;
using Interfold.Auth.Contracts.Ids;
using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Ids;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Interfold.Auth.Api.Auth;

/// <summary>Validates <c>Cf-Access-Jwt-Assertion</c> against the team JWKS. A valid token is
/// already allowlisted at Access — this only checks signature, iss, aud, and email, then
/// reads the Access identity provider so callers can map it onto <see cref="ProviderIdentity"/>.</summary>
public sealed class CloudflareAccessJwtValidator
{
    internal const string JwksUrlOverrideEnv = "INTERFOLD_CF_ACCESS_JWKS_URL";
    internal const string IdentityUrlOverrideEnv = "INTERFOLD_CF_ACCESS_IDENTITY_URL";

    private readonly IOptionsMonitor<CloudflareAccessConfiguration> _options;
    private readonly HttpClient _http;
    private readonly TimeProvider _timeProvider;
    private readonly Lock _gate = new();
    private JsonWebKeySet? _cachedKeys;
    private DateTimeOffset _keysExpireAt;

    public CloudflareAccessJwtValidator(
        IOptionsMonitor<CloudflareAccessConfiguration> options,
        HttpClient http,
        TimeProvider timeProvider)
    {
        _options = options;
        _http = http;
        _timeProvider = timeProvider;
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

            var identityProvider = ReadIdpTypeFromJwt(jwt)
                ?? await TryFetchIdentityProviderAsync(cfg.TeamDomain, assertion, ct).ConfigureAwait(false);
            return CloudflareAccessJwtResult.Ok(email.Trim(), identityProvider);
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

    internal static string BuildIdentityUrl(string teamDomain)
    {
        var overrideUrl = Environment.GetEnvironmentVariable(IdentityUrlOverrideEnv);
        if (!string.IsNullOrWhiteSpace(overrideUrl))
            return overrideUrl.Trim();

        return $"https://{NormalizeTeamHost(teamDomain)}/cdn-cgi/access/get-identity";
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
            if (_cachedKeys is not null && _timeProvider.GetUtcNow() < _keysExpireAt)
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
            _keysExpireAt = _timeProvider.GetUtcNow().AddMinutes(10);
        }

        return keys;
    }

    private async Task<string?> TryFetchIdentityProviderAsync(string teamDomain, string assertion, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, BuildIdentityUrl(teamDomain));
            req.Headers.TryAddWithoutValidation(InterfoldHeaders.CfAccessJwtAssertion, assertion);
            using var response = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            return ReadIdpType(doc.RootElement);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            // JWT already proved the user; Google is the only Access IdP we mint today.
            return null;
        }
    }

    private static string? ReadIdpTypeFromJwt(JwtSecurityToken jwt)
    {
        var raw = jwt.Claims.FirstOrDefault(c => c.Type is "idp" or "identity_provider")?.Value;
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        raw = raw.Trim();
        if (!raw.StartsWith('{'))
            return raw;

        try
        {
            using var doc = JsonDocument.Parse(raw);
            return ReadIdpType(doc.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadIdpType(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.String)
            return root.GetString();

        if (root.TryGetProperty("type", out var typeEl))
            return typeEl.GetString();

        if (root.TryGetProperty("idp", out var idpEl))
            return ReadIdpType(idpEl);

        return null;
    }
}

public readonly record struct CloudflareAccessJwtResult
{
    public bool Succeeded { get; init; }
    public string? Email { get; init; }
    public string? IdentityProvider { get; init; }
    public ErrorCode? Error { get; init; }
    public string? Message { get; init; }

    public static CloudflareAccessJwtResult Ok(string email, string? identityProvider = null)
        => new() { Succeeded = true, Email = email, IdentityProvider = identityProvider };

    public static CloudflareAccessJwtResult Fail(ErrorCode error, string message)
        => new() { Succeeded = false, Error = error, Message = message };

    /// <summary>Maps Google (or an omitted IdP, the bootstrapper default) onto email identity. Other types fail closed.</summary>
    public bool TryToProviderIdentity(out ProviderIdentity identity)
    {
        identity = default;
        if (!Succeeded || string.IsNullOrWhiteSpace(Email))
            return false;

        var idp = IdentityProvider;
        if (string.IsNullOrWhiteSpace(idp) || idp.Equals("google", StringComparison.OrdinalIgnoreCase))
        {
            identity = ProviderIdentity.FromGoogle(new Email(Email));
            return true;
        }

        return false;
    }
}
