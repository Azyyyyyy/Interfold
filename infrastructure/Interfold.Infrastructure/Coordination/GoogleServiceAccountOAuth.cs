using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Interfold.Infrastructure.Coordination;

/// <summary>Mints a Google OAuth access token from a service-account JSON key
/// (JWT bearer grant). Cached until shortly before <c>expires_in</c>.</summary>
internal sealed class GoogleServiceAccountOAuth : IDisposable
{
    internal const string DefaultTokenUri = "https://oauth2.googleapis.com/token";
    internal const string FcmScope = "https://www.googleapis.com/auth/firebase.messaging";

    private static readonly TimeSpan RefreshSkew = TimeSpan.FromSeconds(60);

    private readonly HttpClient _http;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _accessToken;
    private DateTimeOffset _expiresAt;

    public GoogleServiceAccountOAuth(HttpClient http, TimeProvider? clock = null)
    {
        _http = http;
        _clock = clock ?? TimeProvider.System;
    }

    public void Invalidate()
    {
        _accessToken = null;
        _expiresAt = default;
    }

    public async Task<string> GetAccessTokenAsync(
        GoogleServiceAccountCredential credential,
        CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();
        var cached = Volatile.Read(ref _accessToken);
        if (cached is not null && now < _expiresAt)
            return cached;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            now = _clock.GetUtcNow();
            if (_accessToken is not null && now < _expiresAt)
                return _accessToken;

            var jwt = CreateSignedJwt(credential, now);
            var tokenUri = string.IsNullOrWhiteSpace(credential.TokenUri)
                ? DefaultTokenUri
                : credential.TokenUri;

            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
                ["assertion"] = jwt,
            });

            using var response = await _http.PostAsync(tokenUri, content, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Google OAuth token endpoint returned {(int)response.StatusCode}.");

            var parsed = await response.Content
                .ReadFromJsonAsync(FcmJsonContext.Default.GoogleAccessTokenResponse, cancellationToken)
                .ConfigureAwait(false);
            if (parsed?.AccessToken is not { Length: > 0 } token)
                throw new InvalidOperationException("Google OAuth token response missing access_token.");

            var lifetime = parsed.ExpiresIn > 0
                ? TimeSpan.FromSeconds(parsed.ExpiresIn)
                : TimeSpan.FromHours(1);

            _accessToken = token;
            _expiresAt = now + lifetime - RefreshSkew;
            return token;
        }
        finally
        {
            _gate.Release();
        }
    }

    internal static string CreateSignedJwt(GoogleServiceAccountCredential credential, DateTimeOffset now)
    {
        var headerJson = JsonSerializer.Serialize(
            new GoogleServiceAccountJwtHeader("RS256", "JWT", credential.PrivateKeyId),
            FcmJsonContext.Default.GoogleServiceAccountJwtHeader);

        var issuedAt = now.ToUnixTimeSeconds();
        var payloadJson = JsonSerializer.Serialize(
            new GoogleServiceAccountJwtPayload(
                credential.ClientEmail!,
                FcmScope,
                string.IsNullOrWhiteSpace(credential.TokenUri) ? DefaultTokenUri : credential.TokenUri,
                issuedAt,
                issuedAt + 3600),
            FcmJsonContext.Default.GoogleServiceAccountJwtPayload);

        var encodedHeader = Base64UrlEncode(Encoding.UTF8.GetBytes(headerJson));
        var encodedPayload = Base64UrlEncode(Encoding.UTF8.GetBytes(payloadJson));
        var signingInput = $"{encodedHeader}.{encodedPayload}";

        using var rsa = RSA.Create();
        rsa.ImportFromPem(NormalizePem(credential.PrivateKey!).AsSpan());
        var signature = rsa.SignData(
            Encoding.UTF8.GetBytes(signingInput),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        return $"{signingInput}.{Base64UrlEncode(signature)}";
    }

    internal static string NormalizePem(string pem)
        => pem.Replace("\\n", "\n", StringComparison.Ordinal);

    private static string Base64UrlEncode(byte[] data) =>
        Convert.ToBase64String(data)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    public void Dispose() => _gate.Dispose();
}
