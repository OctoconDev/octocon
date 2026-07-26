using Interfold.Auth.Api.Auth;
using Interfold.Auth.Api.Controllers.Base;
using Interfold.Auth.Api.Models;
using Interfold.Auth.Api.Services;
using Interfold.Auth.Contracts.Configuration;
using Interfold.Auth.Contracts.Enums;
using Interfold.Auth.Contracts.Models.Commands;
using Interfold.Auth.Domain;
using Interfold.Shared.Api.Models;
using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models.Read;
using Interfold.Shared.Contracts.Operations;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Interfold.Auth.Api.Controllers;

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

        // Fail-fast: the client is contractually required to pass redirect_uri on Begin so
        // the callback (which reads it back from the octocon_link_redirect_uri cookie) can
        // route the user home after the OAuth round-trip. Detecting it upfront avoids the
        // full challenge round-trip landing in the 400 branch of RedirectToClient — the
        // client sees the same error code, just before the browser leaves for the provider.
        // Sibling AuthController.Begin has the same contract; the check lives here as a
        // per-endpoint guard rather than base-class middleware because only the Begin shape
        // carries the redirect_uri query param.
        var redirectUri = Request.Query[OAuthQueryKeys.RedirectUri].ToString();
        if (string.IsNullOrWhiteSpace(redirectUri))
        {
            Response.Headers[InterfoldHeaders.OperationId] = OperationIds.QueryAuthLinkRequest.Value;
            return BadRequest(new ErrorResponse(
                "Missing client-supplied redirect_uri.",
                ErrorCodes.MissingRedirectUri,
                detail: "Pass redirect_uri on GET /auth/link/{provider} so the link callback knows where to send the result."));
        }

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

        // [AllowAnonymous] endpoint: the middleware doesn't populate the principal, but
        // BuildEnvelope stamps the synthetic AnonymousPrincipalId in that case. The link
        // handler resolves the real system id off the link token itself
        // (ResolveSystemIdByLinkTokenAsync), so command.PrincipalId is never read.
        var envelope = BuildEnvelope(OperationIds.AuthLinkCallback, new LinkOAuthIdentityCommand(linkToken.Value, typedIdentity));
        var commandResult = await _linkHandler.HandleAsync(envelope, HttpContext.RequestAborted);
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
            AccountLinkResult.Success when systemId != null => RedirectToClient(redirectUri),
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

    private IActionResult RedirectToClient(string? redirectUri)
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
