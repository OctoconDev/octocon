using Interfold.Api.Auth;
using Interfold.Api.Services;
using Interfold.Api.Services.Secrets;
using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Configuration;
using Interfold.Shared.Domain.Auth;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Interfold.Auth.Api.DependencyInjection;

/// <summary>Auth feature module — the DI equivalent of the old
/// <c>Program.cs</c> <c>--- Auth ---</c> band. Consumed once from
/// <c>Interfold.Api.Host/Program.cs</c>; ordering constraints in doc §Phase 2 apply
/// (call after <c>AddInterfoldOptions</c>, before <c>AddInterfoldPersistence</c>).</summary>
public static class AuthModuleServiceCollectionExtensions
{
    /// <summary>Registers everything the Auth feature owns: env-bound
    /// <see cref="AuthenticationConfiguration"/> + secrets-patcher, the four command
    /// handlers, the three OAuth HTTP clients, JWT bearer with the ES256 signature
    /// validator, the redirect challenge schemes, and the startup-log hosted service.
    /// Does <b>not</b> register the authorization fallback policy — that stays with the
    /// composition host because it applies to every module's controllers.</summary>
    public static IServiceCollection AddAuthModule(this IServiceCollection services, IConfiguration configuration)
    {
        // Env-bound public fields; [Required] secret fields land via
        // AuthenticationSecretsPostConfigure before ValidateOnStart fires.
        services.AddOptions<AuthenticationConfiguration>()
            .Configure<IConfiguration>(ApplyAuthentication)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<IPostConfigureOptions<AuthenticationConfiguration>, AuthenticationSecretsPostConfigure>();

        services.AddSingleton<AuthenticateOAuthCommandHandler>();
        services.AddSingleton<LinkOAuthIdentityCommandHandler>();
        services.AddSingleton<RecordAuthTokenCommandHandler>();
        services.AddSingleton<RevokeAuthTokenCommandHandler>();

        services.AddHttpClient<GoogleOAuthService>();
        services.AddHttpClient<DiscordOAuthService>();
        services.AddHttpClient<AppleOAuthService>();

        // JWTs are self-issued post-OAuth; no external OIDC issuer to validate, so aud +
        // lifetime only. Read straight off configuration to avoid triggering
        // .ValidateOnStart() before AuthenticationSecretsPostConfigure patches in the
        // [Required] secret fields.
        var jwtAudienceAtBoot = configuration[OctoconEnvKeys.JwtAudience] ?? "octocon";

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer();

        // Configure<TDep>() defers the closure to first-request resolution — post-Build,
        // post-PostConfigure, so IOptionsMonitor<AuthenticationConfiguration>.CurrentValue
        // returns the fully-patched config when SignatureValidator dereferences it.
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptionsMonitor<AuthenticationConfiguration>>((options, authMonitor) =>
            {
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    NameClaimType = "sub",
                    ValidateIssuer = false,
                    ValidateAudience = false,
                    ValidAudience = jwtAudienceAtBoot,
                    ValidateLifetime = true,
                    RequireExpirationTime = true,
                    ClockSkew = TimeSpan.FromMinutes(1),
                    ValidateIssuerSigningKey = false,
                    RequireSignedTokens = true,
                    SignatureValidator = (token, _) =>
                        ValidateJwtTokenSignatureForBearer(token, authMonitor.CurrentValue)
                };
            });

        // Same configuration read as jwtAudienceAtBoot for the same PostConfigure reason.
        services.AddInterfoldAuthChallengeSchemes(
            discordOAuthClientId: configuration[OctoconEnvKeys.DiscordOAuthClientId],
            googleOAuthClientId: configuration[OctoconEnvKeys.GoogleOAuthClientId],
            appleOAuthClientId: configuration[OctoconEnvKeys.AppleOAuthClientId]);

        services.AddHostedService<AuthStartupLogHostedService>();

        return services;
    }

    // Public / env-bound fields only; secret fields land via AuthenticationSecretsPostConfigure.
    // Migrated from shared/Interfold.Infrastructure/DependencyInjection/ConfigurationServiceCollectionExtensions.cs
    // in Phase 2 so the shared spine has no back-reference into the Auth feature.
    private static void ApplyAuthentication(AuthenticationConfiguration opts, IConfiguration config)
    {
        opts.CallbackBaseUrl = config[OctoconEnvKeys.AuthCallbackBaseUrl];
        opts.JwtAuthority = config[OctoconEnvKeys.JwtAuthority] ?? "octocon-local";
        opts.JwtAudience = config[OctoconEnvKeys.JwtAudience] ?? opts.JwtAudience;

        // Client secrets are env-bound as fallback; internal.secrets rows override.
        opts.DiscordOAuthClientId = config[OctoconEnvKeys.DiscordOAuthClientId];
        opts.DiscordOAuthClientSecret = config[OctoconEnvKeys.DiscordOAuthClientSecret];
        opts.GoogleOAuthClientId = config[OctoconEnvKeys.GoogleOAuthClientId];
        opts.GoogleOAuthClientSecret = config[OctoconEnvKeys.GoogleOAuthClientSecret];
        opts.AppleOAuthClientId = config[OctoconEnvKeys.AppleOAuthClientId];
        opts.AppleOAuthClientSecret = config[OctoconEnvKeys.AppleOAuthClientSecret];
    }

    private static SecurityToken ValidateJwtTokenSignatureForBearer(
        string token,
        AuthenticationConfiguration config)
        => JwtEs256Validator.ValidateSignature(token, config.JwtEs256VerificationKeyPems ?? [], useJsonWebToken: true);
}
