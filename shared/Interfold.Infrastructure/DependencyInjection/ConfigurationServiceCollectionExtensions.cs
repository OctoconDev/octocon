using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Configuration;
using Interfold.Shared.Contracts.Enums;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Interfold.Infrastructure.DependencyInjection;

/// <summary>Registers the Interfold strongly-typed options graph. Startup-only options
/// use <see cref="IOptions{TOptions}"/>; live-reloadable ones are wired through
/// <see cref="IOptionsMonitor{TOptions}"/> via <see cref="AddLiveReloadable{TOptions}"/>.</summary>
public static class ConfigurationServiceCollectionExtensions
{
    public static IServiceCollection AddInterfoldOptions(this IServiceCollection services)
    {
        services.AddOptions<ClusterConfiguration>()
            .Configure<IConfiguration>(ApplyCluster)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Cross-field IValidatableObject fires at ValidateOnStart so malformed retry
        // knobs / missing Postgres connection strings fail at boot, not first query.
        services.AddOptions<PersistenceConfiguration>()
            .Configure<IConfiguration>(ApplyPersistence)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Platform variants land via FirebaseClientSecretsPostConfigure; a malformed
        // internal.secrets row surfaces at boot as an InvalidOperationException.
        services.AddOptions<FirebaseClientConfiguration>()
            .Configure<IConfiguration>(ApplyFirebaseClient)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // No [Required] — absent internal.secrets row is a supported state (→ NullFCMService).
        // ValidateOnStart still runs so FcmSecretsPostConfigure fires at boot.
        services.AddOptions<FcmConfiguration>()
            .Configure<IConfiguration>((opts, config) => config.GetSection(FcmConfiguration.SectionName).Bind(opts))
            .ValidateOnStart();

        services.AddOptions<ObservabilityConfiguration>()
            .Configure<IConfiguration>(ApplyObservability)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<TrustOptions>()
            .Configure<IConfiguration>(ApplyTrust)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        AddLiveReloadable<StorageConfiguration>(services, ApplyStorage)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        AddLiveReloadable<SocketConfiguration>(services, ApplySocket)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Startup-only — the ASP.NET Core default CorsPolicyProvider does not rebuild
        // policies on reload. Per-entry [AbsoluteHttpUri] validation runs at boot.
        services.AddOptions<CorsOptions>()
            .Configure<IConfiguration>(ApplyCors)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<ScyllaOverrideOptions>()
            .Configure<IConfiguration>(ApplyScyllaOverride)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<InMemorySecretsSeedOptions>()
            .Configure<IConfiguration>(ApplyInMemorySecretsSeed)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        return services;
    }

    // Configure<IConfiguration>() alone doesn't subscribe IOptionsMonitor to reload
    // tokens — an IOptionsChangeTokenSource is needed to bridge the two.
    private static OptionsBuilder<TOptions> AddLiveReloadable<TOptions>(
        IServiceCollection services,
        Action<TOptions, IConfiguration> apply)
        where TOptions : class
    {
        var builder = services.AddOptions<TOptions>().Configure<IConfiguration>(apply);
        services.AddSingleton<IOptionsChangeTokenSource<TOptions>>(sp =>
            new ConfigurationChangeTokenSource<TOptions>(
                Options.DefaultName, sp.GetRequiredService<IConfiguration>()));
        return builder;
    }

    internal static void ApplyCluster(ClusterConfiguration opts, IConfiguration config)
    {
        // Guard against OCTOCON_NODE_GROUP="" short-circuiting the FLY_PROCESS_GROUP
        // fallback — EnvironmentVariablesConfigurationProvider surfaces "" as a real value.
        opts.NodeGroup = EnumWireExtensions.ParseNodeGroup(
            NullIfEmpty(config[OctoconEnvKeys.FlyProcessGroup])
            ?? NullIfEmpty(config[OctoconEnvKeys.NodeGroup]));
    }

    internal static void ApplyPersistence(PersistenceConfiguration opts, IConfiguration config)
    {
        var keyspace = EnumWireExtensions.ParseScyllaKeyspace(
            config[OctoconEnvKeys.ScyllaKeyspace] ?? "nam");
        opts.Mode = EnumWireExtensions.ParsePersistenceMode(config[OctoconEnvKeys.Persistence]);
        opts.ScyllaKeyspace = keyspace;
        opts.PostgresConnectionString = config[OctoconEnvKeys.PostgresConnection]
            ?? "Host=localhost;Port=5432;Database=interfold;Username=interfold;Password=interfold";
        opts.IsSingleScyllaInstance = bool.TryParse(config[OctoconEnvKeys.SingleScyllaInstance], out var singleKs) && singleKs;
        opts.DbRetryAttempts = TryParseInt(config[OctoconEnvKeys.DbRetryAttempts]) ?? 3;
        opts.DbRetryInitialDelay = TimeSpan.FromMilliseconds(
            TryParseInt(config[OctoconEnvKeys.DbRetryInitialDelayMs]) ?? 100);
        opts.DbRetryMaxDelay = TimeSpan.FromMilliseconds(
            TryParseInt(config[OctoconEnvKeys.DbRetryMaxDelayMs]) ?? 1500);
        opts.HydrationMaxConcurrency = TryParseInt(config[OctoconEnvKeys.HydrationMaxConcurrency]) ?? 8;
    }

    // Every platform variant is populated by FirebaseClientSecretsPostConfigure; nothing to
    // bind from env.
    private static void ApplyFirebaseClient(FirebaseClientConfiguration opts, IConfiguration config)
    {
        opts.Android = null;
        opts.Ios = null;
        opts.Web = null;
    }

    private static void ApplyObservability(ObservabilityConfiguration opts, IConfiguration config)
    {
        // Empty "" would otherwise reach the exporter SDK as a bogus endpoint; null skips.
        opts.OtlpEndpoint = NullIfEmpty(config[OctoconEnvKeys.OtlpEndpoint]);
    }

    private static void ApplyTrust(TrustOptions opts, IConfiguration config)
    {
        opts.RootCaPath = NullIfEmpty(config[OctoconEnvKeys.TrustRootCaPath]);
        opts.RootCaFingerprintPath = NullIfEmpty(config[OctoconEnvKeys.TrustRootCaFingerprintPath]);
    }

    private static void ApplyStorage(StorageConfiguration opts, IConfiguration config)
    {
        opts.AvatarStorageRoot = NullIfEmpty(config[OctoconEnvKeys.AvatarStorageRoot]);
        opts.AvatarPublicBase = NullIfEmpty(config[OctoconEnvKeys.AvatarPublicBase]);
    }

    private static void ApplySocket(SocketConfiguration opts, IConfiguration config)
    {
        opts.BatchBytesThreshold = TryParseInt(config[OctoconEnvKeys.SocketBatchBytesThreshold]);
    }

    // Trailing slashes are stripped for parity with ASP.NET Core's exact-string CORS matcher.
    private static void ApplyCors(CorsOptions opts, IConfiguration config)
    {
        opts.AllowedOrigins = (config[OctoconEnvKeys.CorsAllowedOrigins] ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static origin => origin.TrimEnd('/'))
            .Where(static origin => !string.IsNullOrWhiteSpace(origin))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void ApplyScyllaOverride(ScyllaOverrideOptions opts, IConfiguration config)
    {
        var contactPointsRaw = NullIfEmpty(config[OctoconEnvKeys.ScyllaContactPoints]);
        opts.ContactPoints = contactPointsRaw is null
            ? null
            : contactPointsRaw
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        opts.Port = TryParseInt(config[OctoconEnvKeys.ScyllaPort]);
    }

    private static void ApplyInMemorySecretsSeed(InMemorySecretsSeedOptions opts, IConfiguration config)
    {
        opts.EncryptionPepper = NullIfEmpty(config[OctoconEnvKeys.InMemorySecretsSeedEncryptionPepper]);
        opts.AuthJwtEs256PrivatePem = NullIfEmpty(config[OctoconEnvKeys.InMemorySecretsSeedAuthJwtEs256PrivatePem]);
        opts.AuthDeepLinkSecret = NullIfEmpty(config[OctoconEnvKeys.InMemorySecretsSeedAuthDeepLinkSecret]);
        opts.AuthJwtRsa256PrivatePem = NullIfEmpty(config[OctoconEnvKeys.InMemorySecretsSeedAuthJwtRsa256PrivatePem]);
    }

    private static int? TryParseInt(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return int.TryParse(value, out var result) ? result : null;
    }

    // Bridge for env-var="" landing as "" instead of null in IConfiguration.
    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
