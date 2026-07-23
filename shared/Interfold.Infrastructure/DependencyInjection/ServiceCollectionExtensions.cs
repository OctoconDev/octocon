using System.Collections.Concurrent;
using Interfold.Contracts;
using Interfold.Contracts.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Interfold.Infrastructure.DependencyInjection;

public static partial class ServiceCollectionExtensions
{
    private static readonly ConcurrentDictionary<PersistenceMode, List<Func<IServiceCollection, PersistenceConfiguration, IServiceCollection>>> PersistenceReg = [];
    private static readonly Lock PersistenceRegLock = new();

    internal static void AddPersistenceMode(PersistenceMode persistenceMode, Func<IServiceCollection, PersistenceConfiguration, IServiceCollection> reg)
    {
        lock (PersistenceRegLock)
        {
            var regs = PersistenceReg.GetOrAdd(persistenceMode, _ => []);
            regs.Add(reg);
        }
    }
    
    /// <summary>Registers persistence adapters for <paramref name="mode"/> and layers the
    /// caller's overrides onto the <see cref="Microsoft.Extensions.Options.IOptions{TOptions}"/>
    /// pipeline. Mode-registration lambdas still take a synchronous snapshot because a few
    /// (e.g. InMemoryRegionContext) capture <c>ScyllaKeyspace</c> at registration time.</summary>
    public static IServiceCollection AddInterfoldPersistence(
        this IServiceCollection services,
        PersistenceMode mode,
        PersistenceConfiguration configuration
    ) => AddInterfoldPersistence(services, mode, cfg =>
    {
        cfg.Mode                     = configuration.Mode;
        cfg.ScyllaKeyspace           = configuration.ScyllaKeyspace;
        cfg.PostgresConnectionString = configuration.PostgresConnectionString;
        cfg.IsSingleScyllaInstance   = configuration.IsSingleScyllaInstance;
        cfg.DbRetryAttempts          = configuration.DbRetryAttempts;
        cfg.DbRetryInitialDelay      = configuration.DbRetryInitialDelay;
        cfg.DbRetryMaxDelay          = configuration.DbRetryMaxDelay;
        cfg.HydrationMaxConcurrency  = configuration.HydrationMaxConcurrency;
    });

    public static IServiceCollection AddInterfoldPersistence(
        this IServiceCollection services,
        PersistenceMode mode,
        Action<PersistenceConfiguration>? configure = null
    )
    {
        // Env-bound values live in IOptions<PersistenceConfiguration>; caller overrides
        // are captured here for the mode-registration lambdas.
        var options = new PersistenceConfiguration();
        configure?.Invoke(options);

        // Layer caller overrides onto IOptions so both views agree.
        if (configure is not null)
        {
            services.PostConfigure<PersistenceConfiguration>(configure);
        }

        if (!Enum.IsDefined(mode))
        {
            throw new InvalidOperationException($"Unsupported persistence mode: {mode}");
        }

        if (PersistenceReg.TryGetValue(mode, out var list))
        {
            foreach (Func<IServiceCollection, PersistenceConfiguration, IServiceCollection> func in list)
            {
                func(services, options);
            }

            return services;
        }

        throw new InvalidOperationException($"Persistence mode has not yet been implemented: {mode}");
    }

    // Every domain command-handler singleton previously registered here has moved to its
    // owning feature module's Add<Feature>Module extension:
    //   Alters      -> Interfold.Alters.Api.DependencyInjection.AddAltersModule
    //   Auth        -> Interfold.Auth.Api.DependencyInjection.AddAuthModule
    //   Fronting    -> Interfold.Fronting.Api.DependencyInjection.AddFrontingModule
    //   Friendships -> Interfold.Friendships.Api.DependencyInjection.AddFriendshipsModule
    //   Journals    -> Interfold.Journals.Api.DependencyInjection.AddJournalsModule
    //   Polls       -> Interfold.Polls.Api.DependencyInjection.AddPollsModule
    //   Settings    -> Interfold.Settings.Api.DependencyInjection.AddSettingsModule
    //   Tags        -> Interfold.Tags.Api.DependencyInjection.AddTagsModule
    // AddInterfoldDomainHandlers itself is retained as a no-op for wiring symmetry with the
    // per-feature Add<Feature>Module extensions and to give a place for future cross-cutting
    // domain-handler registrations should they ever exist again.
    public static IServiceCollection AddInterfoldDomainHandlers(this IServiceCollection services) => services;
}

