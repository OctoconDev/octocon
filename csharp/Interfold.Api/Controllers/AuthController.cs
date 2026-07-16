using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Mvc;
using Interfold.Contracts.Enums;
using Interfold.Contracts.Operations;
using Interfold.Api.Services;
using Interfold.Contracts.Configuration;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Infrastructure;
using Interfold.Api.Controllers.Base;
using Interfold.Contracts;
using Interfold.Api.Models;
using Interfold.Contracts.Ids;
using Interfold.Api.Auth;

namespace Interfold.Api.Controllers;

[Route("auth")]
public sealed class AuthController : OAuthControllerBase
{
    private readonly IAccountRepository _accounts;
    private readonly IAuthTokenRevocationRepository _tokenRevocation;
    private readonly IEncryptionStateRepository _encryptionRepository;

    public AuthController(
        IAccountRepository accounts,
        IOptionsMonitor<AuthenticationConfiguration> authOptions,
        IAuthenticationSchemeProvider schemeProvider,
        GoogleOAuthService googleOAuth,
        DiscordOAuthService discordOAuth,
        AppleOAuthService appleOAuth,
        IAuthTokenRevocationRepository tokenRevocation,
        IEncryptionStateRepository encryptionRepository)
        : base(authOptions, schemeProvider, googleOAuth, discordOAuth, appleOAuth)
    {
        _accounts = accounts;
        _tokenRevocation = tokenRevocation;
        _encryptionRepository = encryptionRepository;
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

    //TODO: To ensure route works as expected
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

        await _tokenRevocation.RevokeTokenAsync(jti.Value, HttpContext.RequestAborted);

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
            return StatusCode(StatusCodes.Status403Forbidden, "Failed to authenticate. Did you reload the page or copy-paste the URL?");
        }

        // FindOrCreateSystemIdAsync auto-provisions on miss for all three providers —
        // the name honours the invariant explicitly, so callers see the create-on-miss
        // semantics at the call site rather than having to trust a provider-specific
        // method name.
        var resolvedSystemId = await _accounts.FindOrCreateSystemIdAsync(typedIdentity, HttpContext.RequestAborted);

        if (string.IsNullOrWhiteSpace(resolvedSystemId?.Value))
        {
            return StatusCode(StatusCodes.Status403Forbidden, "Failed to authenticate. Did you use the same account to sign in before?");
        }

        var systemId = resolvedSystemId.Value;

        var encryptionState = await _encryptionRepository.GetAsync(systemId, HttpContext.RequestAborted);
        if (encryptionState?.Salt == null)
        {
            // Mint via EncryptionSalt.NewRandom() so the raw base64 string does not
            // survive as a local (ToString() on EncryptionSalt redacts; on a bare string
            // it would not). Byte width lives inside the wrapper rather than hard-coded here.
            await _encryptionRepository.UpsertAsync(systemId, false, null, EncryptionSalt.NewRandom(), HttpContext.RequestAborted);
        }

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
        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.AddYears(100);

        var token = AuthHelper.CreateToken(authConfig, expiresAt, now, jti, systemId);
        
        // Record the issued token for revocation tracking
        await _tokenRevocation.RecordTokenAsync(jti, systemId, expiresAt, HttpContext.RequestAborted);

        // JWS Compact Serialization: base64url(header).base64url(payload).base64url(signature)
        return token;
    }
    
}
