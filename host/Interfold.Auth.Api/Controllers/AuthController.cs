using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Mvc;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Operations;
using Interfold.Api.Services;
using Interfold.Shared.Contracts.Configuration;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Infrastructure;
using Interfold.Api.Controllers.Base;
using Interfold.Shared.Contracts;
using Interfold.Api.Models;
using Interfold.Shared.Contracts.Ids;
using Interfold.Api.Auth;
using Interfold.Domain.Auth;
using Interfold.Shared.Contracts.Models.Commands;

namespace Interfold.Api.Controllers;

[Route("auth")]
public sealed class AuthController : OAuthControllerBase
{
    private readonly AuthenticateOAuthCommandHandler _authHandler;
    private readonly RecordAuthTokenCommandHandler _recordTokenHandler;
    private readonly RevokeAuthTokenCommandHandler _revokeTokenHandler;

    public AuthController(
        AuthenticateOAuthCommandHandler authHandler,
        IOptionsMonitor<AuthenticationConfiguration> authOptions,
        IAuthenticationSchemeProvider schemeProvider,
        GoogleOAuthService googleOAuth,
        DiscordOAuthService discordOAuth,
        AppleOAuthService appleOAuth,
        RecordAuthTokenCommandHandler recordTokenHandler,
        RevokeAuthTokenCommandHandler revokeTokenHandler)
        : base(authOptions, schemeProvider, googleOAuth, discordOAuth, appleOAuth)
    {
        _authHandler = authHandler;
        _recordTokenHandler = recordTokenHandler;
        _revokeTokenHandler = revokeTokenHandler;
    }

    protected override string CallbackRoutePrefix => "auth";

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
