using Interfold.Auth.Api.Auth;
using Interfold.Auth.Api.Json;
using Interfold.Auth.Api.Models;
using Interfold.Auth.Api.Services;
using Interfold.Shared.Api.Models;
using Interfold.Auth.Contracts.Configuration;
using Interfold.Auth.Contracts.Enums;
using Interfold.Auth.Contracts.Ids;
using Interfold.Shared.Api.Http;
using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Operations;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Interfold.Auth.Api.Http;

internal static class OAuthHttp
{
    private const string OAuthIdentityFailureMessage = "Failed to authenticate. Did you reload the page or copy-paste the URL?";

    public static async Task<ProviderIdentity?> ExtractProviderIdentityAsync(
        HttpContext http,
        OAuthProvider provider,
        DiscordOAuthService discordOAuth,
        GoogleOAuthService googleOAuth,
        AppleOAuthService appleOAuth,
        IOptionsMonitor<AuthenticationConfiguration> authOptions,
        string callbackRoutePrefix)
    {
        switch (provider)
        {
            case OAuthProvider.Discord:
            {
                var code = await GetValueAsync(http, OAuthQueryKeys.Code);
                if (!string.IsNullOrWhiteSpace(code))
                {
                    var redirectUri = BuildCallbackBaseUri(http, provider, authOptions, callbackRoutePrefix);
                    var discordId = await discordOAuth.ExchangeCodeForDiscordIdAsync(code, redirectUri, http.RequestAborted);
                    return discordId is { } id && !string.IsNullOrWhiteSpace(id.Value)
                        ? ProviderIdentity.FromDiscord(id)
                        : null;
                }

                var fallback = await GetValueAsync(http, OAuthQueryKeys.Uid, OAuthQueryKeys.DiscordIdFallback, OAuthQueryKeys.Id);
                return string.IsNullOrWhiteSpace(fallback)
                    ? null
                    : ProviderIdentity.FromDiscord(new(fallback));
            }

            case OAuthProvider.Google:
            {
                var code = await GetValueAsync(http, OAuthQueryKeys.Code);
                if (string.IsNullOrWhiteSpace(code))
                {
                    var directEmail = await GetValueAsync(http, OAuthQueryKeys.Email);
                    return string.IsNullOrWhiteSpace(directEmail)
                        ? null
                        : ProviderIdentity.FromGoogle(new(directEmail));
                }

                var redirectUri = BuildCallbackBaseUri(http, provider, authOptions, callbackRoutePrefix);
                var email = await googleOAuth.ExchangeCodeForEmailAsync(code, redirectUri, http.RequestAborted);
                if (email is { } exchangedEmail && !string.IsNullOrWhiteSpace(exchangedEmail.Value))
                    return ProviderIdentity.FromGoogle(exchangedEmail);

                var fallbackEmail = await GetValueAsync(http, OAuthQueryKeys.Email);
                return string.IsNullOrWhiteSpace(fallbackEmail)
                    ? null
                    : ProviderIdentity.FromGoogle(new(fallbackEmail));
            }

            case OAuthProvider.Apple:
            {
                var code = await GetValueAsync(http, OAuthQueryKeys.Code);
                if (!string.IsNullOrWhiteSpace(code))
                {
                    var redirectUri = BuildCallbackBaseUri(http, provider, authOptions, callbackRoutePrefix);
                    var appleId = await appleOAuth.ExchangeCodeForAppleIdAsync(code, redirectUri, http.RequestAborted);
                    if (appleId is { } exchangedAppleId && !string.IsNullOrWhiteSpace(exchangedAppleId.Value))
                        return ProviderIdentity.FromApple(exchangedAppleId);
                }

                var idToken = await GetValueAsync(http, OAuthQueryKeys.IdToken);
                var sub = appleOAuth.ExtractSubFromJwt(idToken);
                if (sub is { } tokenSub && !string.IsNullOrWhiteSpace(tokenSub.Value))
                    return ProviderIdentity.FromApple(tokenSub);

                var fallback = await GetValueAsync(http, OAuthQueryKeys.Uid, OAuthQueryKeys.AppleIdFallback, OAuthQueryKeys.Id, OAuthQueryKeys.Sub);
                return string.IsNullOrWhiteSpace(fallback)
                    ? null
                    : ProviderIdentity.FromApple(new(fallback));
            }

            default:
                return null;
        }
    }

    public static string BuildCallbackBaseUri(
        HttpContext http,
        OAuthProvider provider,
        IOptionsMonitor<AuthenticationConfiguration> authOptions,
        string callbackRoutePrefix)
    {
        var providerKey = provider.ToWire();
        var authConfig = authOptions.CurrentValue;
        var baseUrl = authConfig.CallbackBaseUrl ?? $"{http.Request.Scheme}://{http.Request.Host}";
        return $"{baseUrl}/{callbackRoutePrefix}/{providerKey}/callback";
    }

    public static async Task<string?> GetValueAsync(HttpContext http, params string[] keys)
    {
        foreach (var key in keys)
        {
            var query = http.Request.Query[key].ToString();
            if (!string.IsNullOrWhiteSpace(query))
                return query;
        }

        if (http.Request.HasFormContentType)
        {
            var form = await http.Request.ReadFormAsync(http.RequestAborted);
            foreach (var key in keys)
            {
                var value = form[key].ToString();
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }
        }

        return null;
    }

    public static string GetChallengeScheme(OAuthProvider provider)
        => provider switch
        {
            OAuthProvider.Discord => OAuthChallengeServiceCollectionExtensions.DiscordSchemeName,
            OAuthProvider.Google => OAuthChallengeServiceCollectionExtensions.GoogleSchemeName,
            OAuthProvider.Apple => OAuthChallengeServiceCollectionExtensions.AppleSchemeName,
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unhandled OAuthProvider."),
        };

    public static void StoreRedirectUriCookie(HttpContext http, string cookieName)
    {
        var redirectUri = http.Request.Query[OAuthQueryKeys.RedirectUri].ToString();
        if (!string.IsNullOrWhiteSpace(redirectUri))
        {
            http.Response.Cookies.Append(cookieName, redirectUri, CookieOptionsFor(http));
        }
    }

    public static void StoreQueryCookie(HttpContext http, string cookieName, string queryKey)
    {
        var value = http.Request.Query[queryKey].ToString();
        if (!string.IsNullOrWhiteSpace(value))
            http.Response.Cookies.Append(cookieName, value, CookieOptionsFor(http));
    }

    public static IResult UnsupportedProviderResponse(string provider)
        => TypedResults.BadRequest(new UnsupportedOAuthProviderResponse(
            "Unsupported OAuth provider.",
            ErrorCodes.InvalidOAuthProvider,
            provider));

    public static IResult OAuthIdentityFailureResponse()
        => TypedResults.StatusCode(StatusCodes.Status403Forbidden);

    public static IResult OAuthIdentityFailureContent()
        => JsonMessage(OAuthIdentityFailureMessage, StatusCodes.Status403Forbidden);

    public static IResult JsonMessage(string message, int statusCode)
        => TypedResults.Json(message, AuthApiJsonContext.Default.String, statusCode: statusCode);

    public static IResult JsonError(ErrorResponse error, int statusCode)
        => TypedResults.Json(error, AuthApiJsonContext.Default.ErrorResponse, statusCode: statusCode);

    public static IResult JsonErrorMessage(ErrorMessageResponse error, int statusCode)
        => TypedResults.Json(error, AuthApiJsonContext.Default.ErrorMessageResponse, statusCode: statusCode);

    public static async Task<IResult?> IssueChallengeIfRegisteredAsync(
        HttpContext http,
        OAuthProvider provider,
        IAuthenticationSchemeProvider schemeProvider,
        IOptionsMonitor<AuthenticationConfiguration> authOptions,
        string callbackRoutePrefix,
        OperationId operationId)
    {
        var challengeScheme = GetChallengeScheme(provider);
        var registeredScheme = await schemeProvider.GetSchemeAsync(challengeScheme);
        if (registeredScheme is null)
            return null;

        var props = new AuthenticationProperties
        {
            RedirectUri = BuildCallbackBaseUri(http, provider, authOptions, callbackRoutePrefix),
        };

        http.Response.Headers[InterfoldHeaders.OperationId] = operationId.Value;
        return TypedResults.Challenge(props, [challengeScheme]);
    }

    private static CookieOptions CookieOptionsFor(HttpContext http) => new()
    {
        HttpOnly = true,
        IsEssential = true,
        Secure = http.Request.IsHttps,
        SameSite = SameSiteMode.Lax,
        MaxAge = TimeSpan.FromMinutes(10),
    };
}
