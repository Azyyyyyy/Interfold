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
using Interfold.Domain.Auth;
using Interfold.Contracts.Models.Commands;

namespace Interfold.Api.Controllers;

[AllowAnonymous]
[Route("auth/link")]
public sealed class AuthLinkController : OAuthControllerBase
{
    private const string LinkTokenCookieName = InterfoldCookieNames.LinkToken;
    private const string RedirectUriCookieName = InterfoldCookieNames.LinkRedirectUri;

    private readonly LinkOAuthIdentityCommandHandler _linkHandler;

    public AuthLinkController(
        LinkOAuthIdentityCommandHandler linkHandler,
        IOptionsMonitor<AuthenticationConfiguration> authOptions,
        IAuthenticationSchemeProvider schemeProvider,
        GoogleOAuthService googleOAuth,
        DiscordOAuthService discordOAuth,
        AppleOAuthService appleOAuth)
        : base(authOptions, schemeProvider, googleOAuth, discordOAuth, appleOAuth)
    {
        _linkHandler = linkHandler;
    }

    protected override string CallbackRoutePrefix => "auth/link";

    [HttpGet("{provider}")]
    public async Task<IActionResult> Begin([FromRoute] string provider)
    {
        if (!provider.TryParseWire<OAuthProvider>(out var oauthProvider))
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
        if (!provider.TryParseWire<OAuthProvider>(out var oauthProvider))
            return UnsupportedProviderResponse(provider);

        // LinkToken.From wraps the query-or-cookie fallback in one call so the null check
        // operates on the typed LinkToken?, not on a bare string local. The null-coalescing
        // chain preserves its shape (query first, cookie second, .From consumes the
        // possibly-null result). A bare string local would stay alive across the null-check
        // / error-return boundary; a redirect-log or exception-message-containing-locals in
        // that window would leak the token verbatim.
        var linkToken = LinkToken.From(await GetValueAsync(OAuthQueryKeys.LinkToken) ?? Request.Cookies[LinkTokenCookieName]);
        if (linkToken is null)
        {
            return StatusCode(StatusCodes.Status403Forbidden, "This link token is invalid or has expired.");
        }

        var identity = await ExtractProviderIdentityAsync(oauthProvider);
        if (identity is not { } typedIdentity)
        {
            return StatusCode(StatusCodes.Status403Forbidden, "Failed to authenticate. Did you reload the page or copy-paste the URL?");
        }

        var commandResult = await _linkHandler.HandleAsync(BuildEnvelope(OperationIds.AuthLinkCallback, new LinkOAuthIdentityCommand(linkToken.Value, typedIdentity)), HttpContext.RequestAborted);
        if (!commandResult.Accepted || commandResult.Result is null)
        {
            Response.Cookies.Delete(LinkTokenCookieName);
            return StatusCode(StatusCodes.Status403Forbidden, "This link token is invalid or has expired.");
        }

        var result = commandResult.Result.Result;
        var systemId = commandResult.Result.SystemId;

        Response.Cookies.Delete(LinkTokenCookieName);

        var redirectUri = Request.Cookies[RedirectUriCookieName];
        Response.Cookies.Delete(RedirectUriCookieName);

        Response.Headers[InterfoldHeaders.OperationId] = OperationIds.AuthLinkCallback.Value;

        return result switch
        {
            AccountLinkResult.Success when systemId != null => RedirectWithSocketEventAsync(redirectUri),
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

    private IActionResult RedirectWithSocketEventAsync(string? redirectUri)
    {
        // The client is responsible for supplying its own redirect_uri on the initial
        // GET /auth/link/{provider}?redirect_uri=... call; the cookie threads it through
        // to here. Absence means the client never set it — surface that loudly rather
        // than papering over with a server-configured fallback.
        if (string.IsNullOrWhiteSpace(redirectUri))
        {
            return BadRequest(new ErrorResponse(
                "Missing client-supplied redirect_uri.",
                ErrorCodes.MissingRedirectUri,
                detail: "Pass redirect_uri on GET /auth/link/{provider} so the link callback knows where to send the result."));
        }

        return Redirect(redirectUri);
    }
}
