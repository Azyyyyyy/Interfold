using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Interfold.Auth.Api.Auth;
using Interfold.Auth.Contracts.Configuration;
using Interfold.Shared.Contracts;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.Tokens;

namespace Interfold.Api.UnitTests.Auth;

public sealed class CloudflareAccessJwtValidatorTests
{
    [Test]
    public async Task DisabledWhenEnvEmpty()
    {
        var validator = CreateValidator(new CloudflareAccessConfiguration(), JsonWebKeySetJson(Rsa()));
        await Assert.That(validator.IsEnabled).IsFalse();
        var result = await validator.ValidateAsync("not-a-jwt", CancellationToken.None);
        await Assert.That(result.Error).IsEqualTo(ErrorCodes.CloudflareAccessUnavailable);
    }

    [Test]
    public async Task MissingHeaderFails()
    {
        var rsa = Rsa();
        var validator = CreateValidator(EnabledConfig(), JsonWebKeySetJson(rsa));
        var result = await validator.ValidateAsync(null, CancellationToken.None);
        await Assert.That(result.Error).IsEqualTo(ErrorCodes.MissingAccessJwt);
    }

    [Test]
    public async Task ValidUserJwtReturnsEmail()
    {
        var rsa = Rsa();
        var token = Mint(rsa, email: "ops@example.com");
        var validator = CreateValidator(EnabledConfig(), JsonWebKeySetJson(rsa));
        var result = await validator.ValidateAsync(token, CancellationToken.None);
        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(result.Email).IsEqualTo("ops@example.com");
        await Assert.That(result.TryToProviderIdentity(out var identity)).IsTrue();
        await Assert.That(identity.Google?.Value).IsEqualTo("ops@example.com");
    }

    [Test]
    public async Task GetIdentityProviderIsCaptured()
    {
        var rsa = Rsa();
        var token = Mint(rsa, email: "ops@example.com");
        var validator = CreateValidator(
            EnabledConfig(),
            JsonWebKeySetJson(rsa),
            identityJson: """{"email":"ops@example.com","idp":{"id":"idp-1","type":"google"}}""");
        var result = await validator.ValidateAsync(token, CancellationToken.None);
        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(result.IdentityProvider).IsEqualTo("google");
        await Assert.That(result.TryToProviderIdentity(out _)).IsTrue();
    }

    [Test]
    public async Task GetIdentitySendsCfAuthorizationCookie()
    {
        var rsa = Rsa();
        var token = Mint(rsa, email: "ops@example.com");
        var handler = new StubCfAccessHandler(JsonWebKeySetJson(rsa), """{"idp":{"type":"google"}}""");
        var validator = new CloudflareAccessJwtValidator(
            new StaticOptions(EnabledConfig()),
            new HttpClient(handler) { BaseAddress = new Uri("https://team.cloudflareaccess.com/") },
            TimeProvider.System);
        await validator.ValidateAsync(token, CancellationToken.None);
        await Assert.That(handler.IdentityCookie).IsEqualTo($"CF_Authorization={token}");
    }

    [Test]
    public async Task UnsupportedIdentityProviderDoesNotMap()
    {
        var rsa = Rsa();
        var token = Mint(rsa, email: "ops@example.com");
        var validator = CreateValidator(
            EnabledConfig(),
            JsonWebKeySetJson(rsa),
            identityJson: """{"idp":{"type":"github"}}""");
        var result = await validator.ValidateAsync(token, CancellationToken.None);
        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(result.IdentityProvider).IsEqualTo("github");
        await Assert.That(result.TryToProviderIdentity(out _)).IsFalse();
    }

    [Test]
    public async Task DiscordIdpIdFromIdentityMapsToDiscord()
    {
        var rsa = Rsa();
        var token = Mint(rsa, email: "shared@example.com");
        var validator = CreateValidator(
            EnabledConfig(discordIdpId: "idp-discord-1"),
            JsonWebKeySetJson(rsa),
            identityJson: """{"idp":{"id":"idp-discord-1","type":"oidc","name":"interfold-discord"},"oidc_fields":{"id":"123456789012345678"}}""");
        var result = await validator.ValidateAsync(token, CancellationToken.None);
        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(result.IdentityProviderId).IsEqualTo("idp-discord-1");
        await Assert.That(result.TryToProviderIdentity(out var identity)).IsTrue();
        await Assert.That(identity.Discord?.Value).IsEqualTo("123456789012345678");
        await Assert.That(identity.Google).IsNull();
    }

    [Test]
    public async Task OtherOidcIdpDoesNotMapToDiscord()
    {
        var rsa = Rsa();
        var token = Mint(rsa, email: "shared@example.com");
        var validator = CreateValidator(
            EnabledConfig(discordIdpId: "idp-discord-1"),
            JsonWebKeySetJson(rsa),
            identityJson: """{"idp":{"id":"idp-other","type":"oidc"},"oidc_fields":{"id":"123456789012345678"}}""");
        var result = await validator.ValidateAsync(token, CancellationToken.None);
        await Assert.That(result.TryToProviderIdentity(out _)).IsFalse();
    }

    [Test]
    public async Task OidcCustomClaimSnowflakeMapsToDiscord()
    {
        var rsa = Rsa();
        var token = Mint(
            rsa,
            email: "ops@example.com",
            extra: [("custom", """{"id":"555666777888999000"}""")]);
        var validator = CreateValidator(
            EnabledConfig(discordIdpId: "idp-discord-1"),
            JsonWebKeySetJson(rsa),
            identityJson: """{"idp":{"id":"idp-discord-1","type":"oidc"}}""");
        var result = await validator.ValidateAsync(token, CancellationToken.None);
        await Assert.That(result.TryToProviderIdentity(out var identity)).IsTrue();
        await Assert.That(identity.Discord?.Value).IsEqualTo("555666777888999000");
    }

    [Test]
    public async Task DigitShapedJwtIdWithoutDiscordIdpUsesGoogleEmail()
    {
        var rsa = Rsa();
        var token = Mint(rsa, email: "shared@example.com", extra: [("id", "123456789012345678")]);
        var validator = CreateValidator(
            EnabledConfig(),
            JsonWebKeySetJson(rsa),
            identityJson: """{"email":"shared@example.com","idp":{"type":"google"}}""");
        var result = await validator.ValidateAsync(token, CancellationToken.None);
        await Assert.That(result.TryToProviderIdentity(out var identity)).IsTrue();
        await Assert.That(identity.Google?.Value).IsEqualTo("shared@example.com");
        await Assert.That(identity.Discord).IsNull();
    }

    [Test]
    public async Task OidcFieldsSubMapsToDiscordWhenIdpIsOurs()
    {
        var rsa = Rsa();
        var token = Mint(rsa, email: "shared@example.com");
        var validator = CreateValidator(
            EnabledConfig(discordIdpId: "idp-discord-1"),
            JsonWebKeySetJson(rsa),
            identityJson: """{"idp":{"id":"idp-discord-1","type":"oidc"},"oidc_fields":{"sub":"987654321098765432"}}""");
        var result = await validator.ValidateAsync(token, CancellationToken.None);
        await Assert.That(result.TryToProviderIdentity(out var identity)).IsTrue();
        await Assert.That(identity.Discord?.Value).IsEqualTo("987654321098765432");
        await Assert.That(identity.Google).IsNull();
    }

    [Test]
    public async Task DiscordIdpWithoutSnowflakeDoesNotMap()
    {
        var rsa = Rsa();
        var token = Mint(rsa, email: "ops@example.com");
        var validator = CreateValidator(
            EnabledConfig(discordIdpId: "idp-discord-1"),
            JsonWebKeySetJson(rsa),
            identityJson: """{"idp":{"id":"idp-discord-1","type":"oidc"}}""");
        var result = await validator.ValidateAsync(token, CancellationToken.None);
        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(result.TryToProviderIdentity(out _)).IsFalse();
    }

    [Test]
    public async Task ServiceTokenWithoutEmailIsRejected()
    {
        var rsa = Rsa();
        var token = Mint(rsa, email: null, commonName: "interfold-bootstrap");
        var validator = CreateValidator(EnabledConfig(), JsonWebKeySetJson(rsa));
        var result = await validator.ValidateAsync(token, CancellationToken.None);
        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(result.Error).IsEqualTo(ErrorCodes.InvalidToken);
    }

    [Test]
    public async Task AudienceMismatchFails()
    {
        var rsa = Rsa();
        var token = Mint(rsa, email: "ops@example.com", aud: "other-aud");
        var validator = CreateValidator(EnabledConfig(), JsonWebKeySetJson(rsa));
        var result = await validator.ValidateAsync(token, CancellationToken.None);
        await Assert.That(result.Succeeded).IsFalse();
    }

    [Test]
    public async Task JwksCacheExpiresOnTimeProvider()
    {
        var rsa = Rsa();
        var jwks = JsonWebKeySetJson(rsa);
        var token = Mint(rsa, email: "ops@example.com");
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var handler = new StubCfAccessHandler(jwks);
        var validator = new CloudflareAccessJwtValidator(
            new StaticOptions(EnabledConfig()),
            new HttpClient(handler) { BaseAddress = new Uri("https://team.cloudflareaccess.com/") },
            clock);

        await validator.ValidateAsync(token, CancellationToken.None);
        await validator.ValidateAsync(token, CancellationToken.None);
        await Assert.That(handler.JwksRequests).IsEqualTo(1);

        clock.Advance(TimeSpan.FromMinutes(11));
        await validator.ValidateAsync(token, CancellationToken.None);
        await Assert.That(handler.JwksRequests).IsEqualTo(2);
    }

    private static CloudflareAccessConfiguration EnabledConfig(string? discordIdpId = null) => new()
    {
        TeamDomain = "team.cloudflareaccess.com",
        Audience = "app-aud",
        DiscordIdentityProviderId = discordIdpId ?? string.Empty,
    };

    private static CloudflareAccessJwtValidator CreateValidator(
        CloudflareAccessConfiguration cfg,
        string jwks,
        string? identityJson = null)
    {
        var handler = new StubCfAccessHandler(jwks, identityJson);
        return new CloudflareAccessJwtValidator(
            new StaticOptions(cfg),
            new HttpClient(handler) { BaseAddress = new Uri("https://team.cloudflareaccess.com/") },
            TimeProvider.System);
    }

    private static RSA Rsa() => RSA.Create(2048);

    private static string Mint(
        RSA rsa,
        string? email,
        string? commonName = null,
        string aud = "app-aud",
        params (string Type, string Value)[] extra)
    {
        var key = new RsaSecurityKey(rsa);
        var creds = new SigningCredentials(key, SecurityAlgorithms.RsaSha256);
        var claims = new List<Claim>();
        if (email is not null)
            claims.Add(new Claim("email", email));
        if (commonName is not null)
            claims.Add(new Claim("common_name", commonName));
        foreach (var (type, value) in extra)
            claims.Add(new Claim(type, value));

        var token = new JwtSecurityToken(
            issuer: "https://team.cloudflareaccess.com",
            audience: aud,
            claims: claims,
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static string JsonWebKeySetJson(RSA rsa)
    {
        var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(new RsaSecurityKey(rsa) { KeyId = "test" });
        jwk.Use = "sig";
        jwk.Alg = "RS256";
        return $$"""{"keys":[{"kty":"RSA","use":"sig","alg":"RS256","kid":"test","n":"{{jwk.N}}","e":"{{jwk.E}}"}]}""";
    }

    private sealed class StaticOptions(CloudflareAccessConfiguration value) : IOptionsMonitor<CloudflareAccessConfiguration>
    {
        public CloudflareAccessConfiguration CurrentValue { get; } = value;
        public CloudflareAccessConfiguration Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<CloudflareAccessConfiguration, string?> listener) => null;
    }

    private sealed class StubCfAccessHandler(string jwks, string? identityJson = null) : HttpMessageHandler
    {
        public int JwksRequests { get; private set; }
        public string? IdentityCookie { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            if (path.Contains("get-identity", StringComparison.Ordinal))
            {
                IdentityCookie = request.Headers.TryGetValues("Cookie", out var cookies)
                    ? string.Join("; ", cookies)
                    : null;
                var body = identityJson ?? """{"idp":{"type":"google"}}""";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                });
            }

            JwksRequests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(jwks, Encoding.UTF8, "application/json"),
            });
        }
    }
}
