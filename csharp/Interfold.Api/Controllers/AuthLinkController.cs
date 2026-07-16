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

        var resolvedSystemId = await _accounts.ResolveSystemIdByLinkTokenAsync(linkToken.Value, HttpContext.RequestAborted);
        if (string.IsNullOrWhiteSpace(resolvedSystemId?.Value))
        {
            Response.Cookies.Delete(LinkTokenCookieName);
            return StatusCode(StatusCodes.Status403Forbidden, "This link token is invalid or has expired.");
        }

        // The link-token map stores scoped ids on write; TryParseScoped enforces the
        // invariant at the read boundary so a legacy row that lost its prefix surfaces
        // here rather than as a bad event target three hops downstream.
        if (!ScopedSystemId.TryParseScoped(resolvedSystemId.Value, out var systemId))
        {
            Response.Cookies.Delete(LinkTokenCookieName);
            return StatusCode(StatusCodes.Status403Forbidden, "This link token is invalid or has expired.");
        }

        await _accounts.ClearLinkTokenAsync(systemId, HttpContext.RequestAborted);
        Response.Cookies.Delete(LinkTokenCookieName);

        var redirectUri = Request.Cookies[RedirectUriCookieName];
        Response.Cookies.Delete(RedirectUriCookieName);

        // ExtractProviderIdentityAsync returns a ProviderIdentity? that the account repo
        // dispatches on directly — dispatch happens off the typed shape rather than an
        // enum + side-band raw string that would need to be re-wrapped at every hop.
        var identity = await ExtractProviderIdentityAsync(oauthProvider);
        if (identity is not { } typedIdentity)
        {
            return StatusCode(StatusCodes.Status403Forbidden, "Failed to authenticate. Did you reload the page or copy-paste the URL?");
        }

        var result = await _accounts.LinkIdentityToUserAsync(systemId, typedIdentity, HttpContext.RequestAborted);

        Response.Headers[InterfoldHeaders.OperationId] = OperationIds.AuthLinkCallback.Value;

        return result switch
        {
            AccountLinkResult.Success => await RedirectWithSocketEventAsync(systemId, typedIdentity, redirectUri),
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

    private async Task<IActionResult> RedirectWithSocketEventAsync(ScopedSystemId systemId, ProviderIdentity identity, string? redirectUri)
    {
        // Dispatch on the ProviderIdentity union so each PublishAsync gets its concrete
        // event type; subscriber routing continues to dispatch by TEvent.
        switch (identity)
        {
            case { Discord: { } discordId }:
                await _eventBus.PublishAsync(
                    new SettingsDiscordAccountLinkedEvent(systemId, discordId),
                    HttpContext.RequestAborted);
                break;
            case { Google: { } email }:
                await _eventBus.PublishAsync(
                    new SettingsGoogleAccountLinkedEvent(systemId, email),
                    HttpContext.RequestAborted);
                break;
            case { Apple: { } appleId }:
                await _eventBus.PublishAsync(
                    new SettingsAppleAccountLinkedEvent(systemId, appleId),
                    HttpContext.RequestAborted);
                break;
        }

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
