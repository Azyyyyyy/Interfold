using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Interfold.Api.Auth;
using Interfold.Api.Services;
using Interfold.Contracts.Configuration;
using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;
using Interfold.Contracts;
using Interfold.Api.Models;

namespace Interfold.Api.Controllers.Base;

public abstract class OAuthControllerBase : InterfoldControllerBase
{
    protected readonly IOptionsMonitor<AuthenticationConfiguration> AuthOptions;
    protected readonly IAuthenticationSchemeProvider SchemeProvider;
    protected readonly GoogleOAuthService GoogleOAuth;
    protected readonly DiscordOAuthService DiscordOAuth;
    protected readonly AppleOAuthService AppleOAuth;

    protected OAuthControllerBase(
        IOptionsMonitor<AuthenticationConfiguration> authOptions,
        IAuthenticationSchemeProvider schemeProvider,
        GoogleOAuthService googleOAuth,
        DiscordOAuthService discordOAuth,
        AppleOAuthService appleOAuth)
    {
        AuthOptions = authOptions;
        SchemeProvider = schemeProvider;
        GoogleOAuth = googleOAuth;
        DiscordOAuth = discordOAuth;
        AppleOAuth = appleOAuth;
    }

    protected abstract string CallbackRoutePrefix { get; }

    protected async Task<string?> ExtractProviderIdentityAsync(OAuthProvider provider)
    {
        switch (provider)
        {
            case OAuthProvider.Discord:
            {
                var code = await GetValueAsync("code");
                if (!string.IsNullOrWhiteSpace(code))
                {
                    var redirectUri = BuildCallbackBaseUri(provider);
                    return await DiscordOAuth.ExchangeCodeForDiscordIdAsync(code, redirectUri, HttpContext.RequestAborted);
                }

                return await GetValueAsync("uid", "discord_id", "id");
            }

            case OAuthProvider.Google:
            {
                var code = await GetValueAsync("code");
                if (string.IsNullOrWhiteSpace(code))
                {
                    return await GetValueAsync("email");
                }

                var redirectUri = BuildCallbackBaseUri(provider);
                var email = await GoogleOAuth.ExchangeCodeForEmailAsync(code, redirectUri, HttpContext.RequestAborted);

                return email ?? await GetValueAsync("email");
            }

            case OAuthProvider.Apple:
            {
                var code = await GetValueAsync("code");
                if (!string.IsNullOrWhiteSpace(code))
                {
                    var redirectUri = BuildCallbackBaseUri(provider);
                    var appleId = await AppleOAuth.ExchangeCodeForAppleIdAsync(code, redirectUri, HttpContext.RequestAborted);
                    if (!string.IsNullOrWhiteSpace(appleId))
                    {
                        return appleId;
                    }
                }

                var idToken = await GetValueAsync("id_token");
                var sub = AppleOAuth.ExtractSubFromJwt(idToken);
                if (!string.IsNullOrWhiteSpace(sub))
                {
                    return sub;
                }

                return await GetValueAsync("uid", "apple_id", "id", "sub");
            }

            default:
                return null;
        }
    }

    protected string BuildCallbackBaseUri(OAuthProvider provider)
    {
        var providerKey = provider.ToWireValue();
        var authConfig = AuthOptions.CurrentValue;
        var baseUrl = authConfig.CallbackBaseUrl ?? $"{Request.Scheme}://{Request.Host}";
        return $"{baseUrl}/{CallbackRoutePrefix}/{providerKey}/callback";
    }

    protected async Task<string?> GetValueAsync(params string[] keys)
    {
        foreach (var key in keys)
        {
            var query = Request.Query[key].ToString();
            if (!string.IsNullOrWhiteSpace(query))
            {
                return query;
            }
        }

        if (Request.HasFormContentType)
        {
            var form = await Request.ReadFormAsync(HttpContext.RequestAborted);
            foreach (var key in keys)
            {
                var value = form[key].ToString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }

        return null;
    }

    protected static string GetChallengeScheme(OAuthProvider provider)
    {
        return provider switch
        {
            OAuthProvider.Discord => OAuthChallengeServiceCollectionExtensions.DiscordSchemeName,
            OAuthProvider.Google => OAuthChallengeServiceCollectionExtensions.GoogleSchemeName,
            OAuthProvider.Apple => OAuthChallengeServiceCollectionExtensions.AppleSchemeName,
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unhandled OAuthProvider."),
        };
    }

    protected void StoreRedirectUriCookie(string cookieName)
    {
        var redirectUri = Request.Query["redirect_uri"].ToString();
        if (!string.IsNullOrWhiteSpace(redirectUri))
        {
            Response.Cookies.Append(cookieName, redirectUri, new CookieOptions
            {
                HttpOnly = true,
                IsEssential = true,
                Secure = Request.IsHttps,
                SameSite = SameSiteMode.Lax,
                MaxAge = TimeSpan.FromMinutes(10)
            });
        }
    }

    protected void StoreQueryCookie(string cookieName, string queryKey)
    {
        var value = Request.Query[queryKey].ToString();
        if (!string.IsNullOrWhiteSpace(value))
        {
            Response.Cookies.Append(cookieName, value, new CookieOptions
            {
                HttpOnly = true,
                IsEssential = true,
                Secure = Request.IsHttps,
                SameSite = SameSiteMode.Lax,
                MaxAge = TimeSpan.FromMinutes(10)
            });
        }
    }

    protected IActionResult UnsupportedProviderResponse(string provider)
    {
        return BadRequest(new UnsupportedOAuthProviderResponse(
            "Unsupported OAuth provider.",
            ErrorCodes.InvalidOAuthProvider,
            provider));
    }

    /// <summary>
    /// Looks up the registered challenge scheme for the supplied provider key and returns a
    /// <see cref="ChallengeResult"/> against it, or <c>null</c> when the scheme isn't
    /// registered (typically because the operator hasn't supplied the matching
    /// <c>OCTOCON_*_OAUTH_CLIENT_ID</c>). The static challenge query parameters and the
    /// authorization endpoint URL both come from <see cref="OAuthChallengeServiceCollectionExtensions"/>
    /// — see that type for why they're baked in rather than threaded through here.
    /// </summary>
    protected async Task<IActionResult?> IssueChallengeIfRegisteredAsync(
        OAuthProvider provider,
        OperationId operationId)
    {
        var challengeScheme = GetChallengeScheme(provider);

        var registeredScheme = await SchemeProvider.GetSchemeAsync(challengeScheme);
        if (registeredScheme is null)
            return null;

        var props = new AuthenticationProperties
        {
            RedirectUri = BuildCallbackBaseUri(provider)
        };

        Response.Headers[InterfoldHeaders.OperationId] = operationId.Value;
        return Challenge(props, challengeScheme);
    }
}
