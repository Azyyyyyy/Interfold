using Interfold.Auth.Api.Auth;
using Interfold.Auth.Api.Http;
using Interfold.Auth.Api.Models;
using Interfold.Auth.Api.Services;
using Interfold.Auth.Contracts.Configuration;
using Interfold.Auth.Contracts.Enums;
using Interfold.Auth.Contracts.Ids;
using Interfold.Auth.Contracts.Models.Commands;
using Interfold.Auth.Domain;
using Interfold.Settings.Contracts.Ids;
using Interfold.Shared.Api.Http;
using Interfold.Shared.Api.Models;
using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Contracts.Models.Read;
using Interfold.Shared.Contracts.Operations;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Interfold.Auth.Api.DependencyInjection;

public static partial class AuthModuleServiceCollectionExtensions
{
    public static IEndpointRouteBuilder MapAuthModule(this IEndpointRouteBuilder endpoints)
    {
        var auth = endpoints.MapGroup("/auth");
        auth.MapGet("/login-methods", LoginMethods).AllowAnonymous();
        auth.MapGet("/cloudflare", CloudflareBegin).AllowAnonymous();
        auth.MapPost("/cloudflare/session", CloudflareSession).AllowAnonymous();
        auth.MapGet("/{provider}", BeginAuth).AllowAnonymous();
        auth.MapGet("/{provider}/callback", CallbackAuthGet).AllowAnonymous();
        auth.MapPost("/{provider}/callback", CallbackAuthPost).AllowAnonymous();
        auth.MapPost("/revoke", RevokeToken);

        var link = endpoints.MapGroup("/auth/link").AllowAnonymous();
        link.MapGet("/{provider}", BeginLink);
        link.MapGet("/{provider}/callback", CallbackLinkGet);
        link.MapPost("/{provider}/callback", CallbackLinkPost);
        return endpoints;
    }

    /// <summary>Anonymous discovery of login buttons. When <c>cloudflare</c> is true,
    /// clients should hide Discord/Apple/Google Interfold buttons.</summary>
    private static IResult LoginMethods(
        HttpContext http,
        IOptionsMonitor<AuthenticationConfiguration> authOptions,
        CloudflareAccessJwtValidator accessJwt)
    {
        var auth = authOptions.CurrentValue;
        http.Response.Headers[InterfoldHeaders.OperationId] = OperationIds.QueryAuthLoginMethods.Value;
        return TypedResults.Ok(new LoginMethodsResponse
        {
            Cloudflare = accessJwt.IsEnabled,
            Google = !string.IsNullOrWhiteSpace(auth.GoogleOAuthClientId),
            Discord = !string.IsNullOrWhiteSpace(auth.DiscordOAuthClientId),
            Apple = !string.IsNullOrWhiteSpace(auth.AppleOAuthClientId),
        });
    }

    /// <summary>
    /// Exchanges a Cloudflare Access JWT for an Interfold session and redirects to
    /// <c>redirect_uri?token=&amp;id=</c>.
    /// </summary>
    private static async Task<IResult> CloudflareBegin(
        HttpContext http,
        string? redirect_uri,
        AuthenticateOAuthCommandHandler authHandler,
        RecordAuthTokenCommandHandler recordTokenHandler,
        CloudflareAccessJwtValidator accessJwt,
        IOptionsMonitor<AuthenticationConfiguration> authOptions)
    {
        if (string.IsNullOrWhiteSpace(redirect_uri))
        {
            http.Response.Headers[InterfoldHeaders.OperationId] = OperationIds.AuthCloudflareExchange.Value;
            return TypedResults.BadRequest(new ErrorResponse(
                "Missing client-supplied redirect_uri.",
                ErrorCodes.MissingRedirectUri,
                detail: "Pass redirect_uri on GET /auth/cloudflare so the callback knows where to send the token."));
        }

        var exchange = await ExchangeAccessJwtAsync(http, authHandler, recordTokenHandler, accessJwt, authOptions);
        if (exchange.Error is { } error)
            return error;

        var separator = redirect_uri.Contains('?') ? '&' : '?';
        var redirectUrl =
            $"{redirect_uri}{separator}{OAuthQueryKeys.CallbackToken}={Uri.EscapeDataString(exchange.Token!)}&{OAuthQueryKeys.CallbackId}={Uri.EscapeDataString(exchange.SystemId!)}";
        http.Response.Headers[InterfoldHeaders.OperationId] = OperationIds.AuthCloudflareExchange.Value;
        return TypedResults.Redirect(redirectUrl);
    }

    /// <summary>Same Access JWT exchange as GET /auth/cloudflare, returning JSON for native/SPA clients.</summary>
    private static async Task<IResult> CloudflareSession(
        HttpContext http,
        AuthenticateOAuthCommandHandler authHandler,
        RecordAuthTokenCommandHandler recordTokenHandler,
        CloudflareAccessJwtValidator accessJwt,
        IOptionsMonitor<AuthenticationConfiguration> authOptions)
    {
        var exchange = await ExchangeAccessJwtAsync(http, authHandler, recordTokenHandler, accessJwt, authOptions);
        if (exchange.Error is { } error)
            return error;

        http.Response.Headers[InterfoldHeaders.OperationId] = OperationIds.AuthCloudflareExchange.Value;
        return TypedResults.Ok(new CloudflareSessionResponse { Token = exchange.Token!, Id = exchange.SystemId! });
    }

    private static async Task<IResult> BeginAuth(
        HttpContext http,
        string provider,
        IAuthenticationSchemeProvider schemeProvider,
        IOptionsMonitor<AuthenticationConfiguration> authOptions)
    {
        if (!provider.TryParseWire<OAuthProvider>(out var oauthProvider))
            return OAuthHttp.UnsupportedProviderResponse(provider);

        OAuthHttp.StoreRedirectUriCookie(http, InterfoldCookieNames.AuthRedirectUri);

        var challenge = await OAuthHttp.IssueChallengeIfRegisteredAsync(
            http, oauthProvider, schemeProvider, authOptions, "auth", OperationIds.QueryAuthOAuthRequest);

        if (challenge is not null)
            return challenge;

        http.Response.Headers[InterfoldHeaders.OperationId] = OperationIds.QueryAuthOAuthRequest.Value;
        return OAuthHttp.JsonMessage(string.Empty, StatusCodes.Status403Forbidden);
    }

    private static Task<IResult> CallbackAuthGet(
        HttpContext http,
        string provider,
        AuthenticateOAuthCommandHandler authHandler,
        RecordAuthTokenCommandHandler recordTokenHandler,
        IOptionsMonitor<AuthenticationConfiguration> authOptions,
        DiscordOAuthService discordOAuth,
        GoogleOAuthService googleOAuth,
        AppleOAuthService appleOAuth)
        => CallbackAuth(http, provider, authHandler, recordTokenHandler, authOptions, discordOAuth, googleOAuth, appleOAuth);

    private static Task<IResult> CallbackAuthPost(
        HttpContext http,
        string provider,
        AuthenticateOAuthCommandHandler authHandler,
        RecordAuthTokenCommandHandler recordTokenHandler,
        IOptionsMonitor<AuthenticationConfiguration> authOptions,
        DiscordOAuthService discordOAuth,
        GoogleOAuthService googleOAuth,
        AppleOAuthService appleOAuth)
        => CallbackAuth(http, provider, authHandler, recordTokenHandler, authOptions, discordOAuth, googleOAuth, appleOAuth);

    /// <summary>Revokes the current authenticated token (logout).</summary>
    private static async Task<IResult> RevokeToken(
        HttpContext http,
        RevokeAuthTokenCommandHandler revokeTokenHandler)
    {
        var jti = Jti.From(http.User.FindFirst(JwtClaimNames.Jti)?.Value);
        if (jti is null)
            return TypedResults.BadRequest(new ErrorResponse("Token is missing JTI claim.", ErrorCodes.InvalidToken));

        await revokeTokenHandler.HandleAsync(http.BuildEnvelope(OperationIds.AuthRevokeToken, new RevokeAuthTokenCommand(jti.Value)), http.RequestAborted);

        http.Response.Headers[InterfoldHeaders.OperationId] = OperationIds.AuthRevokeToken.Value;
        return TypedResults.NoContent();
    }

    private static async Task<IResult> CallbackAuth(
        HttpContext http,
        string provider,
        AuthenticateOAuthCommandHandler authHandler,
        RecordAuthTokenCommandHandler recordTokenHandler,
        IOptionsMonitor<AuthenticationConfiguration> authOptions,
        DiscordOAuthService discordOAuth,
        GoogleOAuthService googleOAuth,
        AppleOAuthService appleOAuth)
    {
        if (!provider.TryParseWire<OAuthProvider>(out var oauthProvider))
            return OAuthHttp.UnsupportedProviderResponse(provider);

        var identity = await OAuthHttp.ExtractProviderIdentityAsync(
            http, oauthProvider, discordOAuth, googleOAuth, appleOAuth, authOptions, "auth");
        if (identity is not { } typedIdentity)
            return OAuthHttp.OAuthIdentityFailureContent();

        var envelope = http.BuildEnvelope(OperationIds.AuthOAuthCallback, new AuthenticateOAuthCommand(typedIdentity));
        var result = await authHandler.HandleAsync(envelope, http.RequestAborted);
        if (!result.Accepted)
            return OAuthHttp.JsonMessage("Failed to authenticate. Did you use the same account to sign in before?", StatusCodes.Status403Forbidden);

        var systemId = result.Result;
        var token = await IssueDeepLinkTokenAsync(http, systemId, recordTokenHandler, authOptions);

        var clientRedirectUri = http.Request.Cookies[InterfoldCookieNames.AuthRedirectUri];
        http.Response.Cookies.Delete(InterfoldCookieNames.AuthRedirectUri);

        if (string.IsNullOrWhiteSpace(clientRedirectUri))
        {
            http.Response.Headers[InterfoldHeaders.OperationId] = OperationIds.AuthOAuthCallback.Value;
            return TypedResults.BadRequest(new ErrorResponse(
                "Missing client-supplied redirect_uri.",
                ErrorCodes.MissingRedirectUri,
                detail: "Pass redirect_uri on GET /auth/{provider} so the callback knows where to send the token."));
        }

        var separator = clientRedirectUri.Contains('?') ? '&' : '?';
        var redirectUrl = $"{clientRedirectUri}{separator}{OAuthQueryKeys.CallbackToken}={Uri.EscapeDataString(token)}&{OAuthQueryKeys.CallbackId}={Uri.EscapeDataString(systemId)}";

        http.Response.Headers[InterfoldHeaders.OperationId] = OperationIds.AuthOAuthCallback.Value;
        return TypedResults.Redirect(redirectUrl);
    }

    private static async Task<(string? Token, string? SystemId, IResult? Error)> ExchangeAccessJwtAsync(
        HttpContext http,
        AuthenticateOAuthCommandHandler authHandler,
        RecordAuthTokenCommandHandler recordTokenHandler,
        CloudflareAccessJwtValidator accessJwt,
        IOptionsMonitor<AuthenticationConfiguration> authOptions)
    {
        var assertion = http.Request.Headers[InterfoldHeaders.CfAccessJwtAssertion].ToString();
        var validated = await accessJwt.ValidateAsync(assertion, http.RequestAborted);
        if (!validated.Succeeded)
        {
            http.Response.Headers[InterfoldHeaders.OperationId] = OperationIds.AuthCloudflareExchange.Value;
            var status = validated.Error == ErrorCodes.CloudflareAccessUnavailable
                ? StatusCodes.Status503ServiceUnavailable
                : StatusCodes.Status401Unauthorized;
            return (null, null, OAuthHttp.JsonError(new ErrorResponse(
                validated.Message ?? "Cloudflare Access JWT rejected.",
                validated.Error ?? ErrorCodes.InvalidToken), status));
        }

        if (!validated.TryToProviderIdentity(out var identity))
        {
            http.Response.Headers[InterfoldHeaders.OperationId] = OperationIds.AuthCloudflareExchange.Value;
            return (null, null, OAuthHttp.JsonError(new ErrorResponse(
                $"Cloudflare Access identity provider '{validated.IdentityProvider}' is not supported.",
                ErrorCodes.UnsupportedAccessIdentityProvider,
                detail: "Interfold maps Access logins from Google or Discord (OIDC)."), StatusCodes.Status403Forbidden));
        }

        var envelope = http.BuildEnvelope(OperationIds.AuthCloudflareExchange, new AuthenticateOAuthCommand(identity));
        var result = await authHandler.HandleAsync(envelope, http.RequestAborted);
        if (!result.Accepted)
        {
            http.Response.Headers[InterfoldHeaders.OperationId] = OperationIds.AuthCloudflareExchange.Value;
            return (null, null, OAuthHttp.JsonMessage("Failed to authenticate. Did you use the same account to sign in before?", StatusCodes.Status403Forbidden));
        }

        var systemId = result.Result;
        var token = await IssueDeepLinkTokenAsync(http, systemId, recordTokenHandler, authOptions);
        return (token, systemId.Value, null);
    }

    private static async Task<string> IssueDeepLinkTokenAsync(
        HttpContext http,
        SystemId systemId,
        RecordAuthTokenCommandHandler recordTokenHandler,
        IOptionsMonitor<AuthenticationConfiguration> authOptions)
    {
        var authConfig = authOptions.CurrentValue;
        var jti = Jti.NewJti();
        var now = http.Time().GetUtcNow();
        var expiresAt = now.AddYears(100);
        var token = AuthHelper.CreateToken(authConfig, expiresAt, now, jti, systemId);

        var envelope = new CommandEnvelope<RecordAuthTokenCommand>(
            OperationIds.AuthOAuthCallback,
            Guid.NewGuid(),
            systemId,
            http.GetIdempotencyKey(),
            http.Time().GetUtcNow(),
            new RecordAuthTokenCommand(jti, systemId, expiresAt));

        await recordTokenHandler.HandleAsync(envelope, http.RequestAborted);
        return token;
    }

    private static async Task<IResult> BeginLink(
        HttpContext http,
        string provider,
        IAuthenticationSchemeProvider schemeProvider,
        IOptionsMonitor<AuthenticationConfiguration> authOptions)
    {
        if (!provider.TryParseWire<OAuthProvider>(out var oauthProvider))
            return OAuthHttp.UnsupportedProviderResponse(provider);

        var redirectUri = http.Request.Query[OAuthQueryKeys.RedirectUri].ToString();
        if (string.IsNullOrWhiteSpace(redirectUri))
        {
            http.Response.Headers[InterfoldHeaders.OperationId] = OperationIds.QueryAuthLinkRequest.Value;
            return TypedResults.BadRequest(new ErrorResponse(
                "Missing client-supplied redirect_uri.",
                ErrorCodes.MissingRedirectUri,
                detail: "Pass redirect_uri on GET /auth/link/{provider} so the link callback knows where to send the result."));
        }

        OAuthHttp.StoreQueryCookie(http, InterfoldCookieNames.LinkToken, OAuthQueryKeys.LinkToken);
        OAuthHttp.StoreRedirectUriCookie(http, InterfoldCookieNames.LinkRedirectUri);

        var challenge = await OAuthHttp.IssueChallengeIfRegisteredAsync(
            http, oauthProvider, schemeProvider, authOptions, "auth/link", OperationIds.QueryAuthLinkRequest);

        if (challenge is not null)
            return challenge;

        http.Response.Headers[InterfoldHeaders.OperationId] = OperationIds.QueryAuthLinkRequest.Value;
        return OAuthHttp.JsonMessage(string.Empty, StatusCodes.Status403Forbidden);
    }

    private static Task<IResult> CallbackLinkGet(
        HttpContext http,
        string provider,
        LinkOAuthIdentityCommandHandler linkHandler,
        IOptionsMonitor<AuthenticationConfiguration> authOptions,
        DiscordOAuthService discordOAuth,
        GoogleOAuthService googleOAuth,
        AppleOAuthService appleOAuth)
        => CallbackLink(http, provider, linkHandler, authOptions, discordOAuth, googleOAuth, appleOAuth);

    private static Task<IResult> CallbackLinkPost(
        HttpContext http,
        string provider,
        LinkOAuthIdentityCommandHandler linkHandler,
        IOptionsMonitor<AuthenticationConfiguration> authOptions,
        DiscordOAuthService discordOAuth,
        GoogleOAuthService googleOAuth,
        AppleOAuthService appleOAuth)
        => CallbackLink(http, provider, linkHandler, authOptions, discordOAuth, googleOAuth, appleOAuth);

    private static async Task<IResult> CallbackLink(
        HttpContext http,
        string provider,
        LinkOAuthIdentityCommandHandler linkHandler,
        IOptionsMonitor<AuthenticationConfiguration> authOptions,
        DiscordOAuthService discordOAuth,
        GoogleOAuthService googleOAuth,
        AppleOAuthService appleOAuth)
    {
        if (!provider.TryParseWire<OAuthProvider>(out var oauthProvider))
            return OAuthHttp.UnsupportedProviderResponse(provider);

        var linkToken = LinkToken.From(await OAuthHttp.GetValueAsync(http, OAuthQueryKeys.LinkToken) ?? http.Request.Cookies[InterfoldCookieNames.LinkToken]);
        if (linkToken is null)
            return OAuthHttp.JsonMessage("This link token is invalid or has expired.", StatusCodes.Status403Forbidden);

        var identity = await OAuthHttp.ExtractProviderIdentityAsync(
            http, oauthProvider, discordOAuth, googleOAuth, appleOAuth, authOptions, "auth/link");
        if (identity is not { } typedIdentity)
            return OAuthHttp.OAuthIdentityFailureContent();

        var envelope = http.BuildEnvelope(OperationIds.AuthLinkCallback, new LinkOAuthIdentityCommand(linkToken.Value, typedIdentity));
        var commandResult = await linkHandler.HandleAsync(envelope, http.RequestAborted);
        if (!commandResult.Accepted || commandResult.Result is null)
        {
            http.Response.Cookies.Delete(InterfoldCookieNames.LinkToken);
            return OAuthHttp.JsonMessage("This link token is invalid or has expired.", StatusCodes.Status403Forbidden);
        }

        var result = commandResult.Result.Result;
        var systemId = commandResult.Result.SystemId;

        http.Response.Cookies.Delete(InterfoldCookieNames.LinkToken);

        var redirectUri = http.Request.Cookies[InterfoldCookieNames.LinkRedirectUri];
        http.Response.Cookies.Delete(InterfoldCookieNames.LinkRedirectUri);

        http.Response.Headers[InterfoldHeaders.OperationId] = OperationIds.AuthLinkCallback.Value;

        return result switch
        {
            AccountLinkResult.Success when systemId != null => RedirectToClient(redirectUri),
            AccountLinkResult.AlreadyLinked => OAuthHttp.JsonErrorMessage(new ErrorMessageResponse(
                oauthProvider switch
                {
                    OAuthProvider.Discord => "A Discord account is already linked to this account; please unlink it first.",
                    OAuthProvider.Google => "A Google account is already linked to this account; please unlink it first.",
                    OAuthProvider.Apple => "An Apple account is already linked to this account; please unlink it first.",
                    _ => "An account is already linked to this account; please unlink it first."
                }), StatusCodes.Status403Forbidden),
            AccountLinkResult.UserExists => OAuthHttp.JsonErrorMessage(new ErrorMessageResponse(
                oauthProvider switch
                {
                    OAuthProvider.Discord => "This Discord account is already linked to another account.",
                    OAuthProvider.Google => "This email address is already linked to another account.",
                    OAuthProvider.Apple => "This Apple account is already linked to another account.",
                    _ => "This account is already linked to another account."
                }), StatusCodes.Status500InternalServerError),
            _ => OAuthHttp.JsonErrorMessage(new ErrorMessageResponse("System not found"), StatusCodes.Status403Forbidden)
        };
    }

    private static IResult RedirectToClient(string? redirectUri)
    {
        if (string.IsNullOrWhiteSpace(redirectUri))
        {
            return TypedResults.BadRequest(new ErrorResponse(
                "Missing client-supplied redirect_uri.",
                ErrorCodes.MissingRedirectUri,
                detail: "Pass redirect_uri on GET /auth/link/{provider} so the link callback knows where to send the result."));
        }

        return TypedResults.Redirect(redirectUri);
    }
}
