using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Mvc;
using Interfold.Domain.Abstractions;
using Interfold.Contracts.Enums;
using Interfold.Contracts.Operations;
using Interfold.Api.Services;
using Interfold.Contracts.Configuration;
using Interfold.Contracts.Events;
using Interfold.Contracts.Models.Read;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Api.Controllers.Base;
using Interfold.Api.Models;
using Interfold.Contracts;
using Interfold.Contracts.Ids;
using Interfold.Api.Auth;

namespace Interfold.Api.Controllers;

[AllowAnonymous]
[Route("auth/link")]
public sealed class AuthLinkController : OAuthControllerBase
{
    private const string LinkTokenCookieName = InterfoldCookieNames.LinkToken;
    private const string RedirectUriCookieName = InterfoldCookieNames.LinkRedirectUri;

    private readonly IAccountRepository _accounts;
    private readonly IClusterEventBus _eventBus;

    public AuthLinkController(
        IAccountRepository accounts,
        IOptionsMonitor<AuthenticationConfiguration> authOptions,
        IAuthenticationSchemeProvider schemeProvider,
        GoogleOAuthService googleOAuth,
        DiscordOAuthService discordOAuth,
        AppleOAuthService appleOAuth,
        IClusterEventBus eventBus)
        : base(authOptions, schemeProvider, googleOAuth, discordOAuth, appleOAuth)
    {
        _accounts = accounts;
        _eventBus = eventBus;
    }

    protected override string CallbackRoutePrefix => "auth/link";

    [HttpGet("{provider}")]
    public async Task<IActionResult> Begin([FromRoute] string provider)
    {
        if (EnumWireExtensions.TryParseOAuthProvider(provider) is not { } oauthProvider)
            return UnsupportedProviderResponse(provider);

        StoreQueryCookie(LinkTokenCookieName, OAuthQueryKeys.LinkToken);
        StoreRedirectUriCookie(RedirectUriCookieName);

        var challenge = await IssueChallengeIfRegisteredAsync(oauthProvider, OperationIds.QueryAuthLinkRequest);

        if (challenge is not null)
            return challenge;

        Response.Headers[InterfoldHeaders.OperationId] = OperationIds.QueryAuthLinkRequest.Value;
        return StatusCode(StatusCodes.Status403Forbidden, string.Empty);
    }

    [HttpGet("{provider}/callback")]
    public Task<IActionResult> CallbackGet([FromRoute] string provider)
        => Callback(provider);

    [HttpPost("{provider}/callback")]
    public Task<IActionResult> CallbackPost([FromRoute] string provider)
        => Callback(provider);

    private async Task<IActionResult> Callback(string provider)
    {
        if (EnumWireExtensions.TryParseOAuthProvider(provider) is not { } oauthProvider)
            return UnsupportedProviderResponse(provider);

        var linkToken = await GetValueAsync(OAuthQueryKeys.LinkToken) ?? Request.Cookies[LinkTokenCookieName];
        if (string.IsNullOrWhiteSpace(linkToken))
        {
            return StatusCode(StatusCodes.Status403Forbidden, "This link token is invalid or has expired.");
        }

        var resolvedSystemId = await _accounts.ResolveSystemIdByLinkTokenAsync(new Interfold.Contracts.Ids.LinkToken(linkToken), HttpContext.RequestAborted);
        if (string.IsNullOrWhiteSpace(resolvedSystemId?.Value))
        {
            Response.Cookies.Delete(LinkTokenCookieName);
            return StatusCode(StatusCodes.Status403Forbidden, "This link token is invalid or has expired.");
        }

        // Slice 4: the link-token map stores scoped ids on write; ParseScoped enforces that
        // invariant at the read boundary so a legacy row that lost its prefix (or a Postgres
        // bootstrap that emitted a bare id) surfaces here rather than as a bad event target
        // three hops downstream.
        if (!Interfold.Contracts.Ids.ScopedSystemId.TryParseScoped(resolvedSystemId.Value.Value, out var systemId))
        {
            Response.Cookies.Delete(LinkTokenCookieName);
            return StatusCode(StatusCodes.Status403Forbidden, "This link token is invalid or has expired.");
        }

        await _accounts.ClearLinkTokenAsync(systemId, HttpContext.RequestAborted);
        Response.Cookies.Delete(LinkTokenCookieName);

        var redirectUri = Request.Cookies[RedirectUriCookieName];
        Response.Cookies.Delete(RedirectUriCookieName);

        var identity = await ExtractProviderIdentityAsync(oauthProvider);
        if (string.IsNullOrWhiteSpace(identity))
        {
            return StatusCode(StatusCodes.Status403Forbidden, "Failed to authenticate. Did you reload the page or copy-paste the URL?");
        }

        var result = oauthProvider switch
        {
            OAuthProvider.Discord => await _accounts.LinkDiscordToUserAsync(systemId, new DiscordId(identity), HttpContext.RequestAborted),
            OAuthProvider.Google => await _accounts.LinkEmailToUserAsync(systemId, new Email(identity), HttpContext.RequestAborted),
            OAuthProvider.Apple => await _accounts.LinkAppleToUserAsync(systemId, new AppleId(identity), HttpContext.RequestAborted),
            _ => AccountLinkResult.UserNotFound
        };

        Response.Headers[InterfoldHeaders.OperationId] = OperationIds.AuthLinkCallback.Value;

        return result switch
        {
            AccountLinkResult.Success => await RedirectWithSocketEventAsync(systemId, oauthProvider, identity, redirectUri),
            AccountLinkResult.AlreadyLinked => StatusCode(StatusCodes.Status403Forbidden, new ErrorMessageResponse(
                oauthProvider switch
                {
                    OAuthProvider.Discord => "A Discord account is already linked to this account; please unlink it first.",
                    OAuthProvider.Google => "A Google account is already linked to this account; please unlink it first.",
                    OAuthProvider.Apple => "An Apple account is already linked to this account; please unlink it first.",
                    _ => "An account is already linked to this account; please unlink it first."
                })),
            AccountLinkResult.UserExists => StatusCode(StatusCodes.Status500InternalServerError, new ErrorMessageResponse(
                oauthProvider switch
                {
                    OAuthProvider.Discord => "This Discord account is already linked to another account.",
                    OAuthProvider.Google => "This email address is already linked to another account.",
                    OAuthProvider.Apple => "This Apple account is already linked to another account.",
                    _ => "This account is already linked to another account."
                })),
            _ => StatusCode(StatusCodes.Status403Forbidden, new ErrorMessageResponse("System not found"))
        };
    }

    private async Task<IActionResult> RedirectWithSocketEventAsync(Interfold.Contracts.Ids.ScopedSystemId systemId, OAuthProvider provider, string identity, string? redirectUri)
    {
        switch (provider)
        {
            case OAuthProvider.Discord:
                await _eventBus.PublishAsync(
                    new SettingsDiscordAccountLinkedEvent(systemId, new DiscordId(identity)),
                    HttpContext.RequestAborted);
                break;
            case OAuthProvider.Google:
                await _eventBus.PublishAsync(
                    new SettingsGoogleAccountLinkedEvent(systemId, new Email(identity)),
                    HttpContext.RequestAborted);
                break;
            case OAuthProvider.Apple:
                await _eventBus.PublishAsync(
                    new SettingsAppleAccountLinkedEvent(systemId, new AppleId(identity)),
                    HttpContext.RequestAborted);
                break;
        }

        // The client is responsible for supplying its own redirect_uri on the initial
        // GET /auth/link/{provider}?redirect_uri=... call; the cookie threads it through
        // to here. Absence means the client never set it — surface that loudly rather
        // than papering over with a server-configured fallback.
        if (string.IsNullOrWhiteSpace(redirectUri))
        {
            return BadRequest(new OAuthRedirectErrorResponse(
                "Missing client-supplied redirect_uri.",
                ErrorCodes.MissingRedirectUri,
                "Pass redirect_uri on GET /auth/link/{provider} so the link callback knows where to send the result."));
        }

        return Redirect(redirectUri);
    }
}
