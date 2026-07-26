namespace Interfold.Auth.Api.Auth;

/// <summary>Registers OAuth challenge redirect schemes. Provider endpoint + scope set are
/// constants; only <c>client_id</c> is per-deployment.</summary>
internal static class OAuthChallengeServiceCollectionExtensions
{
    public const string DiscordSchemeName = "oauth-discord";
    public const string GoogleSchemeName = "oauth-google";
    public const string AppleSchemeName = "oauth-apple";

    public const string DiscordEndpoint = "https://discord.com/api/oauth2/authorize";
    public const string GoogleEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    public const string AppleEndpoint = "https://appleid.apple.com/auth/authorize";

    // scope=identify is the minimum ExtractProviderIdentityAsync needs.
    private static readonly IReadOnlyDictionary<string, string> DiscordParams = new Dictionary<string, string>
    {
        [OAuthQueryKeys.ResponseType] = OAuthQueryKeys.Code,
        [OAuthQueryKeys.Scope] = "identify",
    };

    // userinfo.email is the minimum GoogleOAuthService.ExchangeCodeForEmailAsync needs.
    private static readonly IReadOnlyDictionary<string, string> GoogleParams = new Dictionary<string, string>
    {
        [OAuthQueryKeys.ResponseType] = OAuthQueryKeys.Code,
        [OAuthQueryKeys.Scope] = "https://www.googleapis.com/auth/userinfo.email",
    };

    // Apple requires response_mode=form_post whenever scope includes name/email.
    private static readonly IReadOnlyDictionary<string, string> AppleParams = new Dictionary<string, string>
    {
        [OAuthQueryKeys.ResponseType] = OAuthQueryKeys.Code,
        [OAuthQueryKeys.ResponseMode] = "form_post",
        [OAuthQueryKeys.Scope] = "name email",
    };

    /// <summary>Registers the challenge scheme for each provider whose client ID is set.
    /// Missing client ID → scheme stays unregistered and the challenge falls through to 403.
    /// Takes the three IDs individually so the caller reads them from
    /// <c>builder.Configuration</c> without resolving the options monitor pre-PostConfigure.</summary>
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
