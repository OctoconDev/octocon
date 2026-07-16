using Interfold.Contracts.Configuration;
using Interfold.Infrastructure.DependencyInjection;

namespace Interfold.Api.Auth;

/// <summary>
/// Extension methods for registering OAuth challenge redirect schemes from strongly-typed configuration.
/// </summary>
/// <remarks>
/// Scheme names, authorization endpoints, and the static portions of the challenge query
/// (scopes, response_type, response_mode) are baked in as constants. Each provider serves
/// a single, stable, global URL; the scopes are functionally tied to the data the API
/// extracts in the callback handlers so they can't move without a code change anyway. The
/// only per-deployment value the redirect URL still carries is <c>client_id</c>, which is
/// injected from <c>OCTOCON_*_OAUTH_CLIENT_ID</c> at scheme-registration time.
/// </remarks>
internal static class OAuthChallengeServiceCollectionExtensions
{
    /// <summary>Internal scheme name used to register the Discord challenge handler.</summary>
    public const string DiscordSchemeName = "oauth-discord";

    /// <summary>Internal scheme name used to register the Google challenge handler.</summary>
    public const string GoogleSchemeName = "oauth-google";

    /// <summary>Internal scheme name used to register the Apple challenge handler.</summary>
    public const string AppleSchemeName = "oauth-apple";

    /// <summary>
    /// Discord OAuth 2.0 authorization endpoint. Single global URL — see
    /// <see href="https://discord.com/developers/docs/topics/oauth2"/>.
    /// </summary>
    public const string DiscordEndpoint = "https://discord.com/api/oauth2/authorize";

    /// <summary>
    /// Google OAuth 2.0 / OIDC authorization endpoint. Published in Google's OIDC discovery
    /// document at <see href="https://accounts.google.com/.well-known/openid-configuration"/>.
    /// </summary>
    public const string GoogleEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";

    /// <summary>
    /// Apple Sign-In authorization endpoint. Single global URL — see
    /// <see href="https://developer.apple.com/documentation/sign_in_with_apple/sign_in_with_apple_rest_api"/>.
    /// </summary>
    public const string AppleEndpoint = "https://appleid.apple.com/auth/authorize";

    /// <summary>
    /// Static query parameters appended to Discord's authorize URL. <c>scope=identify</c>
    /// returns the user's basic profile (ID + username) — the minimum
    /// <see cref="Controllers.Base.OAuthControllerBase.ExtractProviderIdentityAsync"/>
    /// needs for the Discord branch.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> DiscordParams = new Dictionary<string, string>
    {
        [OAuthQueryKeys.ResponseType] = OAuthQueryKeys.Code,
        [OAuthQueryKeys.Scope] = "identify",
    };

    /// <summary>
    /// Static query parameters appended to Google's authorize URL. The
    /// <c>userinfo.email</c> scope is the minimum that lets
    /// <see cref="Services.GoogleOAuthService.ExchangeCodeForEmailAsync"/> read the email
    /// claim back; bumping it requires a corresponding code change in the callback handler.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> GoogleParams = new Dictionary<string, string>
    {
        [OAuthQueryKeys.ResponseType] = OAuthQueryKeys.Code,
        [OAuthQueryKeys.Scope] = "https://www.googleapis.com/auth/userinfo.email",
    };

    /// <summary>
    /// Static query parameters appended to Apple's authorize URL. <c>response_mode=form_post</c>
    /// is required by Apple whenever <c>scope</c> includes <c>name</c> or <c>email</c>
    /// (Apple's cross-site response policy); <see cref="Controllers.Base.OAuthControllerBase.GetValueAsync"/>
    /// matches it by reading the form body in the callback. <c>scope=name email</c> is the
    /// minimum set the API actually consumes.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> AppleParams = new Dictionary<string, string>
    {
        [OAuthQueryKeys.ResponseType] = OAuthQueryKeys.Code,
        [OAuthQueryKeys.ResponseMode] = "form_post",
        [OAuthQueryKeys.Scope] = "name email",
    };

    /// <summary>
    /// Registers the Discord / Google / Apple challenge schemes against the hardcoded
    /// provider endpoints + static parameter sets. A scheme is only registered when the
    /// matching OAuth client ID is set; that's the operator's signal that they intend to
    /// use the provider. When the client ID is absent the scheme stays unregistered and
    /// <see cref="Controllers.Base.OAuthControllerBase.IssueChallengeIfRegisteredAsync"/>
    /// falls through to a 403.
    /// <para>
    /// Takes the three client IDs individually rather than a whole
    /// <see cref="AuthenticationConfiguration"/> snapshot so the boundary between "env-bound,
    /// safe to read at builder-time" and "secret-store-sourced, requires the post-configure
    /// pipeline" is explicit at the call site — the caller reads directly from
    /// <c>builder.Configuration</c> to avoid resolving the options monitor before
    /// <c>AuthenticationSecretsPostConfigure</c> patches in the <see cref="RequiredAttribute"/>
    /// secret fields (already populated in the snapshot pre-Build by <c>SecretsPreBuildLoader</c>).
    /// </para>
    /// </summary>
    public static IServiceCollection AddInterfoldAuthChallengeSchemes(
        this IServiceCollection services,
        string? discordOAuthClientId,
        string? googleOAuthClientId,
        string? appleOAuthClientId)
    {
        AddSchemeIfConfigured(services, DiscordSchemeName, DiscordEndpoint, discordOAuthClientId, DiscordParams);
        AddSchemeIfConfigured(services, GoogleSchemeName,  GoogleEndpoint,  googleOAuthClientId,  GoogleParams);
        AddSchemeIfConfigured(services, AppleSchemeName,   AppleEndpoint,   appleOAuthClientId,   AppleParams);

        return services;
    }

    private static void AddSchemeIfConfigured(
        IServiceCollection services,
        string scheme,
        string endpoint,
        string? clientId,
        IReadOnlyDictionary<string, string> baseParameters)
    {
        if (string.IsNullOrWhiteSpace(clientId))
            return;

        // Copy the static template + inject the per-deployment client_id. The handler reads
        // a mutable Dictionary, but it never mutates the contents, so a single fresh copy
        // per scheme registration is enough.
        var parameters = new Dictionary<string, string>(baseParameters, StringComparer.Ordinal)
        {
            [OAuthQueryKeys.ClientId] = clientId,
        };

        services
            .AddAuthentication()
            .AddScheme<RedirectChallengeOptions, RedirectChallengeAuthenticationHandler>(scheme, options =>
            {
                options.AuthorizationEndpoint = endpoint;
                options.AdditionalParameters = parameters;
            });
    }
}
