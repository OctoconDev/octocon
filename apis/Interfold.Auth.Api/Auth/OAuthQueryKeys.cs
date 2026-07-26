namespace Interfold.Auth.Api.Auth;

/// <summary>
/// The OAuth 2.0 / OpenID Connect query- and form-parameter names the auth flows read and
/// write. Wire-frozen: the spellings come from the OAuth spec, the providers' token
/// endpoints, and the legacy client callback contract.
/// </summary>
internal static class OAuthQueryKeys
{
    // Standard OAuth 2.0 authorization/token vocabulary
    public const string Code = "code";
    public const string RedirectUri = "redirect_uri";
    public const string ClientId = "client_id";
    public const string ClientSecret = "client_secret";
    public const string GrantType = "grant_type";
    public const string ResponseType = "response_type";
    public const string ResponseMode = "response_mode";
    public const string Scope = "scope";
    public const string IdToken = "id_token";
    public const string Sub = "sub";

    /// <summary>The <c>grant_type</c> value for the authorization-code exchange.</summary>
    public const string AuthorizationCodeGrant = "authorization_code";

    // Legacy/manual identity fallbacks accepted on the callback query string
    public const string Uid = "uid";
    public const string DiscordIdFallback = "discord_id";
    public const string AppleIdFallback = "apple_id";
    public const string Id = "id";
    public const string Email = "email";

    // Interfold-specific link/callback plumbing
    public const string LinkToken = "link_token";
    public const string CallbackToken = "token";
    public const string CallbackId = "id";
}
