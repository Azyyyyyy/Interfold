using Interfold.Auth.Api.Auth;
using Interfold.Auth.Api.Controllers.Base;
using Interfold.Auth.Api.Models;
using Interfold.Auth.Api.Services;
using Interfold.Auth.Contracts.Configuration;
using Interfold.Auth.Contracts.Ids;
using Interfold.Auth.Contracts.Enums;
using Interfold.Auth.Contracts.Models.Commands;
using Interfold.Auth.Domain;
using Interfold.Shared.Api.Models;
using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Contracts.Operations;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Interfold.Auth.Api.Controllers;

[Route("auth")]
public sealed class AuthController : OAuthControllerBase
{
    private readonly AuthenticateOAuthCommandHandler _authHandler;
    private readonly RecordAuthTokenCommandHandler _recordTokenHandler;
    private readonly RevokeAuthTokenCommandHandler _revokeTokenHandler;
    private readonly CloudflareAccessJwtValidator _accessJwt;

    public AuthController(
        AuthenticateOAuthCommandHandler authHandler,
        IOptionsMonitor<AuthenticationConfiguration> authOptions,
        IAuthenticationSchemeProvider schemeProvider,
        GoogleOAuthService googleOAuth,
        DiscordOAuthService discordOAuth,
        AppleOAuthService appleOAuth,
        RecordAuthTokenCommandHandler recordTokenHandler,
        RevokeAuthTokenCommandHandler revokeTokenHandler,
        CloudflareAccessJwtValidator accessJwt)
        : base(authOptions, schemeProvider, googleOAuth, discordOAuth, appleOAuth)
    {
        _authHandler = authHandler;
        _recordTokenHandler = recordTokenHandler;
        _revokeTokenHandler = revokeTokenHandler;
        _accessJwt = accessJwt;
    }

    protected override string CallbackRoutePrefix => "auth";

    /// <summary>Anonymous discovery of login buttons. When <c>cloudflare</c> is true,
    /// clients should hide Discord/Apple/Google Interfold buttons.</summary>
    [AllowAnonymous]
    [HttpGet("login-methods")]
    public IActionResult LoginMethods()
    {
        var auth = AuthOptions.CurrentValue;
        Response.Headers[InterfoldHeaders.OperationId] = OperationIds.QueryAuthLoginMethods.Value;
        return Ok(new LoginMethodsResponse
        {
            Cloudflare = _accessJwt.IsEnabled,
            Google = !string.IsNullOrWhiteSpace(auth.GoogleOAuthClientId),
            Discord = !string.IsNullOrWhiteSpace(auth.DiscordOAuthClientId),
            Apple = !string.IsNullOrWhiteSpace(auth.AppleOAuthClientId),
        });
    }

    /// <summary>
    /// Exchanges a Cloudflare Access JWT for an Interfold session and redirects to
    /// <c>redirect_uri?token=&amp;id=</c>. Same query shape as Google/Discord/Apple callbacks.
    /// </summary>
    [AllowAnonymous]
    [HttpGet("cloudflare")]
    public async Task<IActionResult> CloudflareBegin([FromQuery(Name = OAuthQueryKeys.RedirectUri)] string? redirectUri)
    {
        if (string.IsNullOrWhiteSpace(redirectUri))
        {
            Response.Headers[InterfoldHeaders.OperationId] = OperationIds.AuthCloudflareExchange.Value;
            return BadRequest(new ErrorResponse(
                "Missing client-supplied redirect_uri.",
                ErrorCodes.MissingRedirectUri,
                detail: "Pass redirect_uri on GET /auth/cloudflare so the callback knows where to send the token."));
        }

        var exchange = await ExchangeAccessJwtAsync();
        if (exchange.Error is { } error)
            return error;

        var separator = redirectUri.Contains('?') ? '&' : '?';
        var redirectUrl =
            $"{redirectUri}{separator}{OAuthQueryKeys.CallbackToken}={Uri.EscapeDataString(exchange.Token!)}&{OAuthQueryKeys.CallbackId}={Uri.EscapeDataString(exchange.SystemId!)}";
        Response.Headers[InterfoldHeaders.OperationId] = OperationIds.AuthCloudflareExchange.Value;
        return Redirect(redirectUrl);
    }

    /// <summary>Same Access JWT exchange as GET /auth/cloudflare, returning JSON for native/SPA clients.</summary>
    [AllowAnonymous]
    [HttpPost("cloudflare/session")]
    public async Task<IActionResult> CloudflareSession()
    {
        var exchange = await ExchangeAccessJwtAsync();
        if (exchange.Error is { } error)
            return error;

        Response.Headers[InterfoldHeaders.OperationId] = OperationIds.AuthCloudflareExchange.Value;
        return Ok(new CloudflareSessionResponse { Token = exchange.Token!, Id = exchange.SystemId! });
    }

    [AllowAnonymous]
    [HttpGet("{provider}")]
    public async Task<IActionResult> Begin([FromRoute] string provider)
    {
        if (!provider.TryParseWire<OAuthProvider>(out var oauthProvider))
            return UnsupportedProviderResponse(provider);

        StoreRedirectUriCookie(InterfoldCookieNames.AuthRedirectUri);

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

    /// <summary>
    /// Revokes the current authenticated token (logout).
    /// Requires authentication. The JTI claim from the current token is extracted and marked as revoked.
    /// After revocation, the token cannot be used for subsequent requests.
    /// </summary>
    [Authorize]
    [HttpPost("revoke")]
    public async Task<IActionResult> RevokeToken()
    {
        // Jti.From wraps the possibly-null JWT claim in one call so the null check operates
        // on the typed Jti?, not on a bare string local. A bare string local would stay
        // alive across the null-check / error-return boundary and any incidental log
        // statement in that window would leak the token id verbatim through raw-string
        // interpolation.
        var jti = Jti.From(User.FindFirst(JwtClaimNames.Jti)?.Value);
        if (jti is null)
        {
            return BadRequest(new ErrorResponse("Token is missing JTI claim.", ErrorCodes.InvalidToken));
        }

        await _revokeTokenHandler.HandleAsync(BuildEnvelope(OperationIds.AuthRevokeToken, new RevokeAuthTokenCommand(jti.Value)), HttpContext.RequestAborted);

        Response.Headers[InterfoldHeaders.OperationId] = OperationIds.AuthRevokeToken.Value;
        return NoContent();
    }

    private async Task<IActionResult> Callback(string provider)
    {
        if (!provider.TryParseWire<OAuthProvider>(out var oauthProvider))
            return UnsupportedProviderResponse(provider);

        // ExtractProviderIdentityAsync returns a ProviderIdentity? that the account repo
        // dispatches on directly — no rewrap of a raw string that was just unwrapped
        // inside the OAuth service.
        var identity = await ExtractProviderIdentityAsync(oauthProvider);
        if (identity is not { } typedIdentity)
        {
            return OAuthIdentityFailureResponse();
        }

        // [AllowAnonymous] endpoint: the middleware doesn't populate the principal, but
        // BuildEnvelope stamps the synthetic AnonymousPrincipalId in that case (the OAuth
        // handler resolves the real system id off the identity itself and never reads
        // command.PrincipalId), so we can share the standard envelope shape.
        var envelope = BuildEnvelope(OperationIds.AuthOAuthCallback, new AuthenticateOAuthCommand(typedIdentity));

        var result = await _authHandler.HandleAsync(envelope, HttpContext.RequestAborted);
        if (!result.Accepted)
        {
            return StatusCode(StatusCodes.Status403Forbidden, "Failed to authenticate. Did you use the same account to sign in before?");
        }

        var systemId = result.Result;

        var token = await IssueDeepLinkTokenAsync(systemId);

        var clientRedirectUri = Request.Cookies[InterfoldCookieNames.AuthRedirectUri];
        Response.Cookies.Delete(InterfoldCookieNames.AuthRedirectUri);

        // The client (web/desktop/mobile) is responsible for supplying its own redirect_uri
        // on the initial GET /auth/{provider}?redirect_uri=... call; we store that in the
        // octocon_auth_redirect_uri cookie there and read it back here after the provider
        // round-trip. A missing cookie means the client either never set it or the cookie
        // was dropped — that's a client bug, surface it loudly rather than papering over it
        // with a server-configured fallback.
        if (string.IsNullOrWhiteSpace(clientRedirectUri))
        {
            Response.Headers[InterfoldHeaders.OperationId] = OperationIds.AuthOAuthCallback.Value;
            return BadRequest(new ErrorResponse(
                "Missing client-supplied redirect_uri.",
                ErrorCodes.MissingRedirectUri,
                detail: "Pass redirect_uri on GET /auth/{provider} so the callback knows where to send the token."));
        }

        var separator = clientRedirectUri.Contains('?') ? '&' : '?';
        var redirectUrl = $"{clientRedirectUri}{separator}{OAuthQueryKeys.CallbackToken}={Uri.EscapeDataString(token)}&{OAuthQueryKeys.CallbackId}={Uri.EscapeDataString(systemId)}";

        Response.Headers[InterfoldHeaders.OperationId] = OperationIds.AuthOAuthCallback.Value;
        return Redirect(redirectUrl);
    }

    private async Task<(string? Token, string? SystemId, IActionResult? Error)> ExchangeAccessJwtAsync()
    {
        var assertion = Request.Headers[InterfoldHeaders.CfAccessJwtAssertion].ToString();
        var validated = await _accessJwt.ValidateAsync(assertion, HttpContext.RequestAborted);
        if (!validated.Succeeded)
        {
            Response.Headers[InterfoldHeaders.OperationId] = OperationIds.AuthCloudflareExchange.Value;
            var status = validated.Error == ErrorCodes.CloudflareAccessUnavailable
                ? StatusCodes.Status503ServiceUnavailable
                : StatusCodes.Status401Unauthorized;
            return (null, null, StatusCode(status, new ErrorResponse(
                validated.Message ?? "Cloudflare Access JWT rejected.",
                validated.Error ?? ErrorCodes.InvalidToken)));
        }

        if (!validated.TryToProviderIdentity(out var identity))
        {
            Response.Headers[InterfoldHeaders.OperationId] = OperationIds.AuthCloudflareExchange.Value;
            return (null, null, StatusCode(StatusCodes.Status403Forbidden, new ErrorResponse(
                $"Cloudflare Access identity provider '{validated.IdentityProvider}' is not supported.",
                ErrorCodes.UnsupportedAccessIdentityProvider,
                detail: "Interfold currently maps Access logins from Google only.")));
        }

        var envelope = BuildEnvelope(OperationIds.AuthCloudflareExchange, new AuthenticateOAuthCommand(identity));
        var result = await _authHandler.HandleAsync(envelope, HttpContext.RequestAborted);
        if (!result.Accepted)
        {
            Response.Headers[InterfoldHeaders.OperationId] = OperationIds.AuthCloudflareExchange.Value;
            return (null, null, StatusCode(StatusCodes.Status403Forbidden, "Failed to authenticate. Did you use the same account to sign in before?"));
        }

        var systemId = result.Result;
        var token = await IssueDeepLinkTokenAsync(systemId);
        return (token, systemId.Value, null);
    }

    private async Task<string> IssueDeepLinkTokenAsync(SystemId systemId)
    {
        var authConfig = AuthOptions.CurrentValue;
        // Jti.NewJti() mints + wraps in one call so the raw JTI string doesn't live as a
        // bare local across the CreateToken and RecordTokenAsync sites. Any incidental
        // log or exception-with-locals between mint and wrap would emit the unredacted
        // JTI; the wrapper's ToString redacts.
        var jti = Jti.NewJti();

        // Set expiry to 100 years in the future. This is practically permanent
        // but avoids DateTimeOffset.MaxValue which can cause int64 overflow on validation.
        // If a token is compromised, it can be revoked explicitly via POST /auth/revoke.
        var now = TimeProvider.GetUtcNow();
        var expiresAt = now.AddYears(100);

        var token = AuthHelper.CreateToken(authConfig, expiresAt, now, jti, systemId);

        // Record the issued token for revocation tracking
        var envelope = new CommandEnvelope<RecordAuthTokenCommand>(
            OperationIds.AuthOAuthCallback,
            Guid.NewGuid(),
            ScopedSystemId.ParseScoped(systemId.Value),
            GetIdempotencyKey(),
            TimeProvider.GetUtcNow(),
            new RecordAuthTokenCommand(jti, systemId, expiresAt)
        );

        await _recordTokenHandler.HandleAsync(envelope, HttpContext.RequestAborted);

        // JWS Compact Serialization: base64url(header).base64url(payload).base64url(signature)
        return token;
    }
}
