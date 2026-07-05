using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Mvc;
using Interfold.Contracts.Enums;
using Interfold.Contracts.Operations;
using Interfold.Api.Services;
using Interfold.Contracts.Configuration;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Infrastructure;
using Interfold.Api.Controllers.Base;
using Interfold.Contracts;
using Interfold.Api.Models;
using Interfold.Contracts.Ids;
using Interfold.Api.Auth;

namespace Interfold.Api.Controllers;

[Route("auth")]
public sealed class AuthController : OAuthControllerBase
{
    private const string RedirectUriCookieName = InterfoldCookieNames.AuthRedirectUri;

    private readonly IAccountRepository _accounts;
    private readonly IAuthTokenRevocationRepository _tokenRevocation;
    private readonly IEncryptionStateRepository _encryptionRepository;

    public AuthController(
        IAccountRepository accounts,
        IOptionsMonitor<AuthenticationConfiguration> authOptions,
        IAuthenticationSchemeProvider schemeProvider,
        GoogleOAuthService googleOAuth,
        DiscordOAuthService discordOAuth,
        AppleOAuthService appleOAuth,
        IAuthTokenRevocationRepository tokenRevocation,
        IEncryptionStateRepository encryptionRepository)
        : base(authOptions, schemeProvider, googleOAuth, discordOAuth, appleOAuth)
    {
        _accounts = accounts;
        _tokenRevocation = tokenRevocation;
        _encryptionRepository = encryptionRepository;
    }

    protected override string CallbackRoutePrefix => "auth";

    [AllowAnonymous]
    [HttpGet("{provider}")]
    public async Task<IActionResult> Begin([FromRoute] string provider)
    {
        if (EnumWireExtensions.TryParseOAuthProvider(provider) is not { } oauthProvider)
            return UnsupportedProviderResponse(provider);

        StoreRedirectUriCookie(RedirectUriCookieName);

        var challenge = await IssueChallengeIfRegisteredAsync(
            oauthProvider, OperationIds.QueryAuthOAuthRequest);

        if (challenge is not null)
            return challenge;

        Response.Headers[InterfoldHeaders.OperationId] = OperationIds.QueryAuthOAuthRequest.Value;
        return StatusCode(StatusCodes.Status403Forbidden, string.Empty);
    }

    [AllowAnonymous]
    [HttpGet("{provider}/callback")]
    public Task<IActionResult> CallbackGet([FromRoute] string provider)
        => Callback(provider);

    [AllowAnonymous]
    [HttpPost("{provider}/callback")]
    public Task<IActionResult> CallbackPost([FromRoute] string provider)
        => Callback(provider);

    //TODO: To ensure route works as expected
    /// <summary>
    /// Revokes the current authenticated token (logout).
    /// Requires authentication. The JTI claim from the current token is extracted and marked as revoked.
    /// After revocation, the token cannot be used for subsequent requests.
    /// </summary>
    [Authorize]
    [HttpPost("revoke")]
    public async Task<IActionResult> RevokeToken()
    {
        var jti = User.FindFirst(JwtClaimNames.Jti)?.Value;
        if (string.IsNullOrWhiteSpace(jti))
        {
            return BadRequest(new ErrorResponse("Token is missing JTI claim.", ErrorCodes.InvalidToken));
        }

        await _tokenRevocation.RevokeTokenAsync(new Interfold.Contracts.Ids.Jti(jti), HttpContext.RequestAborted);

        Response.Headers[InterfoldHeaders.OperationId] = OperationIds.AuthRevokeToken.Value;
        return NoContent();
    }

    private async Task<IActionResult> Callback(string provider)
    {
        if (EnumWireExtensions.TryParseOAuthProvider(provider) is not { } oauthProvider)
            return UnsupportedProviderResponse(provider);

        var identity = await ExtractProviderIdentityAsync(oauthProvider);
        if (string.IsNullOrWhiteSpace(identity))
        {
            return StatusCode(StatusCodes.Status403Forbidden, "Failed to authenticate. Did you reload the page or copy-paste the URL?");
        }

        var resolvedSystemId = oauthProvider switch
        {
            OAuthProvider.Discord => await _accounts.FindSystemIdByDiscordIdAsync(new DiscordId(identity), HttpContext.RequestAborted),
            OAuthProvider.Google => await _accounts.FindSystemIdByEmailAsync(new Email(identity), HttpContext.RequestAborted),
            OAuthProvider.Apple => await _accounts.FindSystemIdByAppleIdAsync(new AppleId(identity), HttpContext.RequestAborted),
            _ => null
        };

        if (string.IsNullOrWhiteSpace(resolvedSystemId?.Value))
        {
            return StatusCode(StatusCodes.Status403Forbidden, "Failed to authenticate. Did you use the same account to sign in before?");
        }

        var systemId = resolvedSystemId.Value;

        var encryptionState = await _encryptionRepository.GetAsync(systemId, HttpContext.RequestAborted);
        if (encryptionState?.Salt == null)
        {
            // Generate per-user salt (32 random bytes, Base64-encoded)
            var saltBytes = RandomNumberGenerator.GetBytes(32);
            var salt = Convert.ToBase64String(saltBytes);

            await _encryptionRepository.UpsertAsync(systemId, false, null, salt, HttpContext.RequestAborted);
        }

        var token = await IssueDeepLinkTokenAsync(systemId);

        var clientRedirectUri = Request.Cookies[RedirectUriCookieName];
        Response.Cookies.Delete(RedirectUriCookieName);

        // The client (web/desktop/mobile) is responsible for supplying its own redirect_uri
        // on the initial GET /auth/{provider}?redirect_uri=... call; we store that in the
        // octocon_auth_redirect_uri cookie there and read it back here after the provider
        // round-trip. A missing cookie means the client either never set it or the cookie
        // was dropped — that's a client bug, surface it loudly rather than papering over it
        // with a server-configured fallback.
        if (string.IsNullOrWhiteSpace(clientRedirectUri))
        {
            Response.Headers[InterfoldHeaders.OperationId] = OperationIds.AuthOAuthCallback.Value;
            return BadRequest(new OAuthRedirectErrorResponse(
                "Missing client-supplied redirect_uri.",
                ErrorCodes.MissingRedirectUri,
                "Pass redirect_uri on GET /auth/{provider} so the callback knows where to send the token."));
        }

        var separator = clientRedirectUri.Contains('?') ? '&' : '?';
        var redirectUrl = $"{clientRedirectUri}{separator}{OAuthQueryKeys.CallbackToken}={Uri.EscapeDataString(token)}&{OAuthQueryKeys.CallbackId}={Uri.EscapeDataString(systemId.Value)}";

        Response.Headers[InterfoldHeaders.OperationId] = OperationIds.AuthOAuthCallback.Value;
        return Redirect(redirectUrl);
    }

    private async Task<string> IssueDeepLinkTokenAsync(Interfold.Contracts.Ids.SystemId systemId)
    {
        var authConfig = AuthOptions.CurrentValue;
        var jti = Guid.NewGuid().ToString("N");

        // Set expiry to 100 years in the future. This is practically permanent
        // but avoids DateTimeOffset.MaxValue which can cause int64 overflow on validation.
        // If a token is compromised, it can be revoked explicitly via POST /auth/revoke.
        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.AddYears(100);

        var token = AuthHelper.CreateToken(authConfig, expiresAt, now, new Interfold.Contracts.Ids.Jti(jti), systemId);
        
        // Record the issued token for revocation tracking
        await _tokenRevocation.RecordTokenAsync(new Interfold.Contracts.Ids.Jti(jti), systemId, expiresAt, HttpContext.RequestAborted);

        // JWS Compact Serialization: base64url(header).base64url(payload).base64url(signature)
        return token;
    }
    
}
