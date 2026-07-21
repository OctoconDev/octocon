using Interfold.Contracts;
using Interfold.Contracts.Configuration;
using Interfold.Contracts.Enums;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Interfold.Infrastructure.DependencyInjection;

/// <summary>
/// Extension methods for registering strongly-typed configuration from environment variables.
/// Uses .NET configuration binding with custom providers to ensure compatibility with
/// existing OCTOCON_* and platform-specific (FLY_*) environment variables.
/// </summary>
public static class ConfigurationServiceCollectionExtensions
{
    /// <summary>
    /// Registers all Interfold configuration classes with the DI container using the
    /// <see cref="IOptions{TOptions}"/> / <see cref="IOptionsMonitor{TOptions}"/> pattern.
    /// <para>
    /// Call this once in Program.cs instead of manually binding and registering singletons.
    /// Configuration is read from the live <see cref="IConfiguration"/> on each access, so
    /// values backed by <c>appsettings.json</c> (with <c>reloadOnChange: true</c>) will update
    /// without a restart when consumed via <see cref="IOptionsMonitor{TOptions}"/>.
    /// Environment-variable-backed values are fixed at process start.
    /// </para>
    /// Usage in services:
    /// <list type="bullet">
    ///   <item><see cref="IOptions{TOptions}"/> — startup-only, single snapshot (Persistence, Cluster)</item>
    ///   <item><see cref="IOptionsMonitor{TOptions}"/> — live reload, safe for singletons (Auth, Storage, Socket)</item>
    ///   <item><see cref="IOptionsSnapshot{TOptions}"/> — per-request reload, safe for scoped services</item>
    /// </list>
    /// </summary>
    public static IServiceCollection AddInterfoldOptions(this IServiceCollection services)
    {
        // Startup-only: node role cannot change while the process is running.
        services.AddOptions<ClusterConfiguration>()
            .Configure<IConfiguration>(ApplyCluster)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Startup-only: database connection pools are created once; reconnection requires restart.
        // Validate ranges + the cross-field max >= initial constraint (IValidatableObject on
        // PersistenceConfiguration) at DI-container build time so malformed retry knobs or
        // an empty Postgres connection string fail with a clear boot-time error instead of
        // manifesting as a mysterious first-query hang.
        services.AddOptions<PersistenceConfiguration>()
            .Configure<IConfiguration>(ApplyPersistence)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Startup-baked hybrid: env-bound public fields + internal.secrets-sourced secret
        // fields (patched in by AuthenticationSecretsPostConfigure). Post-configure runs
        // between Configure and validate inside the options factory, so [Required] on the
        // four mandatory secret fields (EncryptionPepper, DeepLinkSecret,
        // JwtEs256PrivateKeyPem, Rsa256PrivateKey) trips ValidateOnStart at boot when the
        // matching internal.secrets row is missing.
        services.AddOptions<AuthenticationConfiguration>()
            .Configure<IConfiguration>(ApplyAuthentication)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Startup-baked: platform variants are deserialised from internal.secrets by
        // FirebaseClientSecretsPostConfigure. ValidateOnStart forces the post-configure to
        // run at boot so a malformed row surfaces immediately (ParseOrThrow raises
        // InvalidOperationException, which OptionsFactory bubbles up like a validation
        // failure).
        services.AddOptions<FirebaseClientConfiguration>()
            .Configure<IConfiguration>(ApplyFirebaseClient)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Startup-baked: the FCM v1 service-account credential is deserialised from
        // internal.secrets by FcmSecretsPostConfigure. No [Required] and no
        // ValidateDataAnnotations here — the row is opt-in per deployment (absent row →
        // NullFCMService fallback in the IFCMService DI factory) — but ValidateOnStart still
        // forces the post-configure to run at boot so the snapshot lookup path is exercised
        // the same way the other secret-store-sourced options are. Bind onto the
        // Octocon:Fcm section for symmetry with the other typed options; the section has no
        // env-var backing today, so the initial value is whatever ServiceAccountJson defaults
        // to (null) and FcmSecretsPostConfigure supplies the runtime value.
        services.AddOptions<FcmConfiguration>()
            .Configure<IConfiguration>((opts, config) => config.GetSection(FcmConfiguration.SectionName).Bind(opts))
            .ValidateOnStart();

        // Registered for completeness; OTLP exporters are wired at startup so runtime changes
        // to OtlpEndpoint only take effect after a restart. ValidateOnStart runs
        // [AbsoluteHttpUri] on the (optional) endpoint so a garbled env var trips at boot,
        // not on the first exporter connection attempt.
        services.AddOptions<ObservabilityConfiguration>()
            .Configure<IConfiguration>(ApplyObservability)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Trust-distribution paths read by TrustController. The values are filesystem paths
        // pointing into the read-only /certs bind mount; they change only on
        // bootstrap --rotate-certs (which restarts the API container), so an
        // IOptions<T> snapshot taken at startup is correct. Validation is opted-in here so
        // an operator error like a relative path lands as a boot failure with the offending
        // env var named — matches the strictness the bootstrapper's config gate applies on
        // config.trust.rootCaPath / rootCaFingerprintPath.
        services.AddOptions<TrustOptions>()
            .Configure<IConfiguration>(ApplyTrust)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Live-reloadable: avatar storage paths can be updated via appsettings.json.
        // ValidateOnStart still fires at the initial bind — reloads are eventual-consistency,
        // not fail-fast.
        AddLiveReloadable<StorageConfiguration>(services, ApplyStorage)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Live-reloadable: batch tuning can be adjusted without restart.
        AddLiveReloadable<SocketConfiguration>(services, ApplySocket)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Startup-only: CORS origins baked into the CorsPolicy at builder-time. Live-reload
        // would require rebuilding the CorsPolicy, which ASP.NET Core's default
        // CorsPolicyProvider does not do. Per-entry [AbsoluteHttpUri] validation lives on
        // the options class (IValidatableObject) so an operator that pushes a non-http
        // origin trips at boot with the offending entry called out.
        services.AddOptions<CorsOptions>()
            .Configure<IConfiguration>(ApplyCors)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Startup-only: Scylla host-port + contact-point overrides for integration tests.
        // Production stacks leave both null and the client falls through to the secrets
        // store. No numeric-range validation on the port here — ScyllaConfigResolver's
        // consumer surfaces a friendlier "no override, fall back" branch that we don't
        // want fail-fast validation to short-circuit.
        services.AddOptions<ScyllaOverrideOptions>()
            .Configure<IConfiguration>(ApplyScyllaOverride)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Startup-only: in-memory secrets seed. Blank values are legal and skipped by the
        // consumer (SecretsBootstrapService is the sole fail-fast for the mandatory rows).
        services.AddOptions<InMemorySecretsSeedOptions>()
            .Configure<IConfiguration>(ApplyInMemorySecretsSeed)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        return services;
    }

    /// <summary>
    /// Registers a live-reloadable strongly-typed options instance. Calling
    /// <c>AddOptions&lt;T&gt;().Configure&lt;IConfiguration&gt;(...)</c> on its own only registers an
    /// <see cref="IConfigureOptions{TOptions}"/> — that wires the apply callback into the snapshot
    /// pipeline, but it does NOT subscribe <see cref="IOptionsMonitor{TOptions}"/> to configuration
    /// reload events. Without an <see cref="IOptionsChangeTokenSource{TOptions}"/> the first access
    /// to <c>CurrentValue</c> caches whatever the apply callback produced and ignores subsequent
    /// <see cref="IConfigurationRoot.Reload"/> calls. <see cref="ConfigurationChangeTokenSource{TOptions}"/>
    /// bridges the configuration's reload token into the options pipeline so consumers actually see
    /// updates, which is what the integration tests' <c>WithConfiguration</c> live-reload contract
    /// depends on.
    /// </summary>
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

    // --- Bind helpers (thin wrappers used by CLI and other non-DI callers) ---



    // --- Apply methods: single source of truth for each configuration mapping ---

    internal static void ApplyCluster(ClusterConfiguration opts, IConfiguration config)
    {
        // .NET's EnvironmentVariablesConfigurationProvider surfaces an env var set to ""
        // as a configuration key with value "" (not null), which breaks the ?? fall-through
        // pattern below. The bootstrapper now always emits OCTOCON_NODE_GROUP (even when the
        // operator leaves the cluster section at its defaults), so guard against the empty-
        // string case explicitly via NullIfEmpty — otherwise Fly stacks that also have
        // OCTOCON_NODE_GROUP="" would short-circuit before reading FLY_PROCESS_GROUP.
        opts.NodeGroup = EnumWireExtensions.ParseNodeGroup(
            NullIfEmpty(config[OctoconEnvKeys.FlyProcessGroup])
            ?? NullIfEmpty(config[OctoconEnvKeys.NodeGroup]));
    }

    internal static void ApplyPersistence(PersistenceConfiguration opts, IConfiguration config)
    {
        // Fail-fast on operator typos: ParseScyllaKeyspace throws for unknown region spellings.
        var keyspace = EnumWireExtensions.ParseScyllaKeyspace(
            config[OctoconEnvKeys.ScyllaKeyspace] ?? "nam");
        opts.Mode = EnumWireExtensions.ParsePersistenceMode(config[OctoconEnvKeys.Persistence]);
        opts.ScyllaKeyspace = keyspace;
        opts.PostgresConnectionString = config[OctoconEnvKeys.PostgresConnection]
            ?? "Host=localhost;Port=5432;Database=interfold;Username=interfold;Password=interfold";
        opts.IsSingleScyllaInstance = bool.TryParse(config[OctoconEnvKeys.SingleScyllaInstance], out var singleKs) && singleKs;
        opts.DbRetryAttempts = TryParseInt(config[OctoconEnvKeys.DbRetryAttempts]) ?? 3;
        // Wire form is integer milliseconds (external contract); convert once to TimeSpan
        // here so DatabaseTransientRetry and other consumers work in strongly-typed
        // durations without re-parsing ms at every call site.
        opts.DbRetryInitialDelay = TimeSpan.FromMilliseconds(
            TryParseInt(config[OctoconEnvKeys.DbRetryInitialDelayMs]) ?? 100);
        opts.DbRetryMaxDelay = TimeSpan.FromMilliseconds(
            TryParseInt(config[OctoconEnvKeys.DbRetryMaxDelayMs]) ?? 1500);
        opts.HydrationMaxConcurrency = TryParseInt(config[OctoconEnvKeys.HydrationMaxConcurrency]) ?? 8;
    }

    /// <summary>
    /// Initial bind of <see cref="AuthenticationConfiguration"/> from env. Public / env-bound
    /// fields land here; every secret-store-sourced field (JWT signing keys, deep-link HMAC
    /// secret, encryption pepper, OAuth client secrets) is layered on top by
    /// <c>AuthenticationSecretsPostConfigure</c> inside the options factory pipeline before
    /// <c>.ValidateOnStart()</c> runs.
    /// </summary>
    private static void ApplyAuthentication(AuthenticationConfiguration opts, IConfiguration config)
    {
        opts.CallbackBaseUrl = config[OctoconEnvKeys.AuthCallbackBaseUrl];
        opts.JwtAuthority = config[OctoconEnvKeys.JwtAuthority] ?? "octocon-local";
        // OCTOCON_JWT_AUDIENCE was documented but never bound — bind it now so the
        // bootstrapper's value flows through. Falls back to the property-initialiser
        // default ("octocon") when the env var is unset so existing callers keep working.
        opts.JwtAudience = config[OctoconEnvKeys.JwtAudience] ?? opts.JwtAudience;

        // OAuth client IDs are public values (they appear in OAuth redirect URLs); keep them
        // env-bound. The matching client secrets are env-bound as a fallback here, then
        // overwritten by AuthenticationSecretsPostConfigure when the corresponding
        // internal.secrets row is present — env wins when the store row is absent.
        opts.DiscordOAuthClientId = config[OctoconEnvKeys.DiscordOAuthClientId];
        opts.DiscordOAuthClientSecret = config[OctoconEnvKeys.DiscordOAuthClientSecret];
        opts.GoogleOAuthClientId = config[OctoconEnvKeys.GoogleOAuthClientId];
        opts.GoogleOAuthClientSecret = config[OctoconEnvKeys.GoogleOAuthClientSecret];
        opts.AppleOAuthClientId = config[OctoconEnvKeys.AppleOAuthClientId];
        opts.AppleOAuthClientSecret = config[OctoconEnvKeys.AppleOAuthClientSecret];

        // JWT signing material, deep-link secret, and encryption pepper are intentionally
        // left at their property-initialiser defaults here. AuthenticationSecretsPostConfigure
        // runs after this apply callback, reads the values from the SecretsSnapshot that
        // SecretsPreBuildLoader primed pre-Build, and .ValidateOnStart() enforces [Required]
        // on the four mandatory fields — a missing row surfaces as an
        // OptionsValidationException naming the offending property.

        // The OAuth challenge query parameters (scopes / response_type / response_mode) plus
        // each provider's scheme name + authorization endpoint are constants in
        // OAuthChallengeServiceCollectionExtensions — the scopes are functionally tied to
        // the data the callback handlers read, so changing them requires a code change. The
        // only per-deployment value (client_id) is injected directly during scheme
        // registration from the OAuthClientId fields above.
    }

    /// <summary>
    /// Initial bind of <see cref="FirebaseClientConfiguration"/>. Every platform variant is
    /// left at its default (<c>null</c>) — <c>FirebaseClientSecretsPostConfigure</c>
    /// deserialises the three optional <c>internal.secrets:firebase:client:{android,ios,web}</c>
    /// rows onto the options instance inside the factory pipeline. A missing row is a
    /// supported state (returns 503 for that platform) so there is nothing to bind from env
    /// vars.
    /// </summary>
    private static void ApplyFirebaseClient(FirebaseClientConfiguration opts, IConfiguration config)
    {
        opts.Android = null;
        opts.Ios = null;
        opts.Web = null;
    }

    private static void ApplyObservability(ObservabilityConfiguration opts, IConfiguration config)
    {
        // The OTLP endpoint is optional — null means "skip exporter registration entirely".
        // The bootstrapper now always emits OCTOCON_OTLP_ENDPOINT (even when blank), so an
        // empty env var would otherwise land here as the literal string "" and confuse the
        // OTLP exporter SDK (it would attempt to connect to ""). Normalise to null so the
        // not-configured branch in the telemetry registration still fires.
        opts.OtlpEndpoint = NullIfEmpty(config[OctoconEnvKeys.OtlpEndpoint]);
    }

    /// <summary>
    /// Binds the OCTOCON_TRUST_* env vars onto <see cref="TrustOptions"/>. Empty strings
    /// (the bootstrapper always emits these env vars, even in dev where the values are
    /// blank) are normalised to <c>null</c> so TrustController's "trust artefacts not
    /// configured" 404 branch fires correctly instead of treating "" as a usable path.
    /// </summary>
    private static void ApplyTrust(TrustOptions opts, IConfiguration config)
    {
        opts.RootCaPath = NullIfEmpty(config[OctoconEnvKeys.TrustRootCaPath]);
        opts.RootCaFingerprintPath = NullIfEmpty(config[OctoconEnvKeys.TrustRootCaFingerprintPath]);
    }

    private static void ApplyStorage(StorageConfiguration opts, IConfiguration config)
    {
        // Both avatar fields are optional — null on each disables the corresponding API
        // surface. The bootstrapper emits empty strings for the unset case (its always-
        // emit-every-parameter contract), so normalise empty → null here to preserve the
        // pre-bootstrapper behaviour where an unset env var produced a null on read.
        opts.AvatarStorageRoot = NullIfEmpty(config[OctoconEnvKeys.AvatarStorageRoot]);
        opts.AvatarPublicBase = NullIfEmpty(config[OctoconEnvKeys.AvatarPublicBase]);
    }

    private static void ApplySocket(SocketConfiguration opts, IConfiguration config)
    {
        opts.BatchBytesThreshold = TryParseInt(config[OctoconEnvKeys.SocketBatchBytesThreshold]);
    }

    /// <summary>
    /// Parses <c>OCTOCON_CORS_ALLOWED_ORIGINS</c> into <see cref="CorsOptions.AllowedOrigins"/>.
    /// Trailing slashes are stripped and comparison is case-insensitive for parity with the
    /// ASP.NET Core CORS matcher, which does exact-string matching against the resulting list.
    /// Blank env-var → empty list (caller is responsible for the "empty means allow-any"
    /// dev-only fallback).
    /// </summary>
    private static void ApplyCors(CorsOptions opts, IConfiguration config)
    {
        opts.AllowedOrigins = (config[OctoconEnvKeys.CorsAllowedOrigins] ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static origin => origin.TrimEnd('/'))
            .Where(static origin => !string.IsNullOrWhiteSpace(origin))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Parses the two test-only Scylla override env vars onto <see cref="ScyllaOverrideOptions"/>.
    /// Blank/missing values leave the properties null so <c>ScyllaConfigResolver</c> can
    /// distinguish "no override — use the secrets-store row" from "operator forced a value".
    /// </summary>
    private static void ApplyScyllaOverride(ScyllaOverrideOptions opts, IConfiguration config)
    {
        var contactPointsRaw = NullIfEmpty(config[OctoconEnvKeys.ScyllaContactPoints]);
        opts.ContactPoints = contactPointsRaw is null
            ? null
            : contactPointsRaw
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        opts.Port = TryParseInt(config[OctoconEnvKeys.ScyllaPort]);
    }

    /// <summary>
    /// Copies the four <c>OCTOCON_INMEMORY_SECRETS_SEED:*</c> configuration values onto
    /// <see cref="InMemorySecretsSeedOptions"/>. Blank/missing values remain null so the
    /// consumer's "skip silently" contract stays intact — SecretsBootstrapService is the
    /// sole fail-fast for the mandatory rows.
    /// </summary>
    private static void ApplyInMemorySecretsSeed(InMemorySecretsSeedOptions opts, IConfiguration config)
    {
        opts.EncryptionPepper = NullIfEmpty(config[OctoconEnvKeys.InMemorySecretsSeedEncryptionPepper]);
        opts.AuthJwtEs256PrivatePem = NullIfEmpty(config[OctoconEnvKeys.InMemorySecretsSeedAuthJwtEs256PrivatePem]);
        opts.AuthDeepLinkSecret = NullIfEmpty(config[OctoconEnvKeys.InMemorySecretsSeedAuthDeepLinkSecret]);
        opts.AuthJwtRsa256PrivatePem = NullIfEmpty(config[OctoconEnvKeys.InMemorySecretsSeedAuthJwtRsa256PrivatePem]);
    }

    // --- Helpers ---

    private static int? TryParseInt(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return int.TryParse(value, out var result) ? result : null;
    }

    /// <summary>
    /// Returns <c>null</c> for null, empty, or whitespace-only inputs; otherwise the input
    /// unchanged. Used to bridge the gap between the .NET configuration system (which
    /// surfaces env-var-set-to-"" as a key with empty-string value) and binders whose
    /// "feature disabled" signal is <c>null</c>.
    /// </summary>
    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
