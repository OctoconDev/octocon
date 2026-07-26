using Interfold.Auth.Api.Auth;
using Interfold.Auth.Api.Models;
using Interfold.Auth.Api.Services;
using Interfold.Auth.Contracts.Configuration;
using Interfold.Auth.Contracts.Enums;
using Interfold.Auth.Contracts.Ids;
using Interfold.Shared.Api.Controllers.Base;
using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Interfold.Auth.Api.Controllers.Base;

public abstract class OAuthControllerBase : InterfoldControllerBase
{
    private const string OAuthIdentityFailureMessage = "Failed to authenticate. Did you reload the page or copy-paste the URL?";

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

    /// <summary>
    /// Resolves the provider identity for the current callback request as a
    /// <see cref="ProviderIdentity"/> union whose populated field carries the typed
    /// identity (Discord snowflake / email / Apple sub). Returns <c>null</c> when no
    /// identity could be extracted (missing code, missing fallback, or failed exchange).
    /// The union carries the typed identity end-to-end so callers never have to switch on
    /// the provider enum and rewrap a raw string.
    /// </summary>
    protected async Task<ProviderIdentity?> ExtractProviderIdentityAsync(OAuthProvider provider)
    {
        switch (provider)
        {
            case OAuthProvider.Discord:
            {
                var code = await GetValueAsync(OAuthQueryKeys.Code);
                if (!string.IsNullOrWhiteSpace(code))
                {
                    var redirectUri = BuildCallbackBaseUri(provider);
                    var discordId = await DiscordOAuth.ExchangeCodeForDiscordIdAsync(code, redirectUri, HttpContext.RequestAborted);
                    return discordId is { } id && !string.IsNullOrWhiteSpace(id.Value)
                        ? ProviderIdentity.FromDiscord(id)
                        : null;
                }

                var fallback = await GetValueAsync(OAuthQueryKeys.Uid, OAuthQueryKeys.DiscordIdFallback, OAuthQueryKeys.Id);
                return string.IsNullOrWhiteSpace(fallback)
                    ? null
                    : ProviderIdentity.FromDiscord(new(fallback));
            }

            case OAuthProvider.Google:
            {
                var code = await GetValueAsync(OAuthQueryKeys.Code);
                if (string.IsNullOrWhiteSpace(code))
                {
                    var directEmail = await GetValueAsync(OAuthQueryKeys.Email);
                    return string.IsNullOrWhiteSpace(directEmail)
                        ? null
                        : ProviderIdentity.FromGoogle(new(directEmail));
                }

                var redirectUri = BuildCallbackBaseUri(provider);
                var email = await GoogleOAuth.ExchangeCodeForEmailAsync(code, redirectUri, HttpContext.RequestAborted);
                if (email is { } exchangedEmail && !string.IsNullOrWhiteSpace(exchangedEmail.Value))
                {
                    return ProviderIdentity.FromGoogle(exchangedEmail);
                }

                var fallbackEmail = await GetValueAsync(OAuthQueryKeys.Email);
                return string.IsNullOrWhiteSpace(fallbackEmail)
                    ? null
                    : ProviderIdentity.FromGoogle(new(fallbackEmail));
            }

            case OAuthProvider.Apple:
            {
                var code = await GetValueAsync(OAuthQueryKeys.Code);
                if (!string.IsNullOrWhiteSpace(code))
                {
                    var redirectUri = BuildCallbackBaseUri(provider);
                    var appleId = await AppleOAuth.ExchangeCodeForAppleIdAsync(code, redirectUri, HttpContext.RequestAborted);
                    if (appleId is { } exchangedAppleId && !string.IsNullOrWhiteSpace(exchangedAppleId.Value))
                    {
                        return ProviderIdentity.FromApple(exchangedAppleId);
                    }
                }

                var idToken = await GetValueAsync(OAuthQueryKeys.IdToken);
                var sub = AppleOAuth.ExtractSubFromJwt(idToken);
                if (sub is { } tokenSub && !string.IsNullOrWhiteSpace(tokenSub.Value))
                {
                    return ProviderIdentity.FromApple(tokenSub);
                }

                var fallback = await GetValueAsync(OAuthQueryKeys.Uid, OAuthQueryKeys.AppleIdFallback, OAuthQueryKeys.Id, OAuthQueryKeys.Sub);
                return string.IsNullOrWhiteSpace(fallback)
                    ? null
                    : ProviderIdentity.FromApple(new(fallback));
            }

            default:
                return null;
        }
    }

    protected string BuildCallbackBaseUri(OAuthProvider provider)
    {
        var providerKey = provider.ToWire();
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
        var redirectUri = Request.Query[OAuthQueryKeys.RedirectUri].ToString();
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

    protected IActionResult OAuthIdentityFailureResponse()
        => StatusCode(StatusCodes.Status403Forbidden, OAuthIdentityFailureMessage);

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
