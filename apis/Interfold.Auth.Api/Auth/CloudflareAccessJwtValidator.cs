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

            var hints = ReadIdentityHintsFromJwt(jwt);
            if (NeedsIdentityFetch(hints, cfg.DiscordIdentityProviderId))
            {
                var fetched = await TryFetchIdentityHintsAsync(cfg.TeamDomain, assertion, ct).ConfigureAwait(false);
                hints = MergeHints(hints, fetched);
            }

            return CloudflareAccessJwtResult.Ok(
                email.Trim(),
                hints.Label,
                hints.DiscordId,
                hints.IdpId,
                cfg.DiscordIdentityProviderId);
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

    private async Task<AccessIdentityHints> TryFetchIdentityHintsAsync(string teamDomain, string assertion, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, BuildIdentityUrl(teamDomain));
            req.Headers.TryAddWithoutValidation("Cookie", $"CF_Authorization={assertion}");
            using var response = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return default;

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            return ReadIdentityHints(doc.RootElement);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            return default;
        }
    }

    private static AccessIdentityHints ReadIdentityHintsFromJwt(JwtSecurityToken jwt)
    {
        var hints = default(AccessIdentityHints);
        var idpRaw = jwt.Claims.FirstOrDefault(c => c.Type is "idp" or "identity_provider")?.Value;
        if (!string.IsNullOrWhiteSpace(idpRaw))
        {
            var raw = idpRaw.Trim();
            if (raw.StartsWith('{'))
            {
                try
                {
                    using var doc = JsonDocument.Parse(raw);
                    hints = ReadIdentityHints(doc.RootElement);
                }
                catch (JsonException)
                {
                    // malformed idp JSON; leave unset and fall through to get-identity
                }
            }
            else
            {
                hints = hints with { IdpName = raw, IdpType = raw };
            }
        }

        var fromCustom = ReadDiscordSnowflakeFromCustomClaim(jwt);
        if (fromCustom is not null)
            hints = hints with { DiscordId = hints.DiscordId ?? fromCustom };

        return hints;
    }

    private static bool NeedsIdentityFetch(AccessIdentityHints hints, string? expectedDiscordIdpId)
    {
        if (!string.IsNullOrWhiteSpace(expectedDiscordIdpId))
            return true;

        return string.IsNullOrWhiteSpace(hints.IdpId)
            && string.IsNullOrWhiteSpace(hints.IdpType)
            && string.IsNullOrWhiteSpace(hints.IdpName);
    }

    private static AccessIdentityHints MergeHints(AccessIdentityHints primary, AccessIdentityHints fallback)
        => new(
            string.IsNullOrWhiteSpace(primary.IdpId) ? fallback.IdpId : primary.IdpId,
            string.IsNullOrWhiteSpace(primary.IdpType) ? fallback.IdpType : primary.IdpType,
            string.IsNullOrWhiteSpace(primary.IdpName) ? fallback.IdpName : primary.IdpName,
            string.IsNullOrWhiteSpace(primary.DiscordId) ? fallback.DiscordId : primary.DiscordId);

    private static string? ReadDiscordSnowflakeFromCustomClaim(JwtSecurityToken jwt)
    {
        var custom = jwt.Claims.FirstOrDefault(c => c.Type == "custom")?.Value;
        if (string.IsNullOrWhiteSpace(custom) || !custom.TrimStart().StartsWith('{'))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(custom);
            return ReadDiscordIdFromOidcClaims(doc.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static AccessIdentityHints ReadIdentityHints(JsonElement root)
    {
        string? id = null;
        string? type = null;
        string? name = null;
        var idp = root;
        if (root.TryGetProperty("idp", out var nested) && nested.ValueKind is JsonValueKind.Object or JsonValueKind.String)
            idp = nested;

        if (idp.ValueKind == JsonValueKind.String)
        {
            type = idp.GetString();
            name = type;
        }
        else if (idp.ValueKind == JsonValueKind.Object)
        {
            if (idp.TryGetProperty("id", out var idEl))
                id = idEl.GetString();
            if (idp.TryGetProperty("type", out var typeEl))
                type = typeEl.GetString();
            if (idp.TryGetProperty("name", out var nameEl))
                name = nameEl.GetString();
        }

        return new AccessIdentityHints(id, type, name, ReadDiscordIdFromOidcClaims(root));
    }

    private static string? ReadDiscordIdFromOidcClaims(JsonElement root)
    {
        if (root.TryGetProperty("oidc_fields", out var fields) && fields.ValueKind == JsonValueKind.Object)
        {
            var fromFields = ReadSnowflakeProperty(fields, "id") ?? ReadSnowflakeProperty(fields, "sub");
            if (fromFields is not null)
                return fromFields;
        }

        if (root.TryGetProperty("custom", out var custom) && custom.ValueKind == JsonValueKind.Object)
            return ReadSnowflakeProperty(custom, "id") ?? ReadSnowflakeProperty(custom, "sub");

        return ReadSnowflakeProperty(root, "id");
    }

    private static string? ReadSnowflakeProperty(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var el))
            return null;

        return ReadDiscordSnowflake(el.ValueKind == JsonValueKind.String ? el.GetString() : el.ToString());
    }

    internal static bool IsDiscordAccessIdp(string? idpId, string? expectedIdpId)
        => !string.IsNullOrWhiteSpace(expectedIdpId)
           && !string.IsNullOrWhiteSpace(idpId)
           && expectedIdpId.Equals(idpId, StringComparison.OrdinalIgnoreCase);

    internal static string? ReadDiscordSnowflake(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var value = raw.Trim();
        if (value.Length < 5 || !value.All(char.IsAsciiDigit))
            return null;

        return value;
    }

    private readonly record struct AccessIdentityHints(
        string? IdpId,
        string? IdpType,
        string? IdpName,
        string? DiscordId)
    {
        public string? Label => IdpType ?? IdpName;
    }
}

public readonly record struct CloudflareAccessJwtResult
{
    public bool Succeeded { get; init; }
    public string? Email { get; init; }
    public string? IdentityProvider { get; init; }
    public string? IdentityProviderId { get; init; }
    public string? ExpectedDiscordIdentityProviderId { get; init; }
    public string? DiscordId { get; init; }
    public ErrorCode? Error { get; init; }
    public string? Message { get; init; }

    public static CloudflareAccessJwtResult Ok(
        string email,
        string? identityProvider = null,
        string? discordId = null,
        string? identityProviderId = null,
        string? expectedDiscordIdentityProviderId = null)
        => new()
        {
            Succeeded = true,
            Email = email,
            IdentityProvider = identityProvider,
            IdentityProviderId = identityProviderId,
            ExpectedDiscordIdentityProviderId = expectedDiscordIdentityProviderId,
            DiscordId = discordId,
        };

    public static CloudflareAccessJwtResult Fail(ErrorCode error, string message)
        => new() { Succeeded = false, Error = error, Message = message };

    /// <summary>Discord only when Access IdP id matches the configured Discord IdP.</summary>
    public bool TryToProviderIdentity(out ProviderIdentity identity)
    {
        identity = default;
        if (!Succeeded)
            return false;

        var isDiscord = CloudflareAccessJwtValidator.IsDiscordAccessIdp(
            IdentityProviderId, ExpectedDiscordIdentityProviderId);
        if (isDiscord)
        {
            if (string.IsNullOrWhiteSpace(DiscordId))
                return false;

            identity = ProviderIdentity.FromDiscord(new DiscordId(DiscordId));
            return true;
        }

        if (string.IsNullOrWhiteSpace(Email))
            return false;

        var idp = IdentityProvider;
        var omitted = string.IsNullOrWhiteSpace(idp) && string.IsNullOrWhiteSpace(IdentityProviderId);
        if (!string.IsNullOrWhiteSpace(ExpectedDiscordIdentityProviderId) && omitted)
            return false;

        if (omitted || IsGoogleAccessIdp(idp))
        {
            identity = ProviderIdentity.FromGoogle(new Email(Email));
            return true;
        }

        return false;
    }

    private static bool IsGoogleAccessIdp(string? idp)
        => !string.IsNullOrWhiteSpace(idp)
           && idp.Equals("google", StringComparison.OrdinalIgnoreCase);
}
