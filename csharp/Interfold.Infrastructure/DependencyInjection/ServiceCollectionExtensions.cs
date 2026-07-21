using System.Collections.Concurrent;
using Interfold.Contracts;
using Interfold.Contracts.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Interfold.Domain.Accounts;
using Interfold.Domain.Alters;
using Interfold.Domain.Friendships;
using Interfold.Domain.Fronting;
using Interfold.Domain.Journals;
using Interfold.Domain.Polls;
using Interfold.Domain.Settings;
using Interfold.Domain.Tags;
using Interfold.Domain.Auth;

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
    
    /// <summary>
    /// Registers the persistence adapters for <paramref name="mode"/>, layering the caller-
    /// supplied snapshot values on top of the env-bound
    /// <see cref="Microsoft.Extensions.Options.IOptions{TOptions}"/> pipeline. Consumers
    /// resolve <see cref="Microsoft.Extensions.Options.IOptions{PersistenceConfiguration}"/>
    /// (single source of truth).
    ///
    /// The mode-registration lambdas still receive a synchronous
    /// <see cref="PersistenceConfiguration"/> snapshot because a few of them (e.g.
    /// InMemoryRegionContext) capture <c>ScyllaKeyspace</c> at registration time — the
    /// snapshot here is built from the caller's overrides only, and matches what the
    /// container will hand to IOptions consumers once the layered PostConfigure runs.
    /// </summary>
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
        // Snapshot for the mode-registration lambdas' synchronous capture (e.g.
        // InMemoryRegionContext takes ScyllaKeyspace at registration time). Only the
        // caller's configure delegate contributes here — the env-bound values are
        // owned by AddInterfoldOptions.ApplyPersistence and reach consumers via
        // IOptions<PersistenceConfiguration>.
        var options = new PersistenceConfiguration();
        configure?.Invoke(options);

        // Layer the caller-supplied overrides onto the options pipeline as a
        // PostConfigure so IOptions<PersistenceConfiguration> consumers see the same
        // values the mode-registration lambdas captured above. Without this the two
        // views would drift for anything the caller set.
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

    public static IServiceCollection AddInterfoldDomainHandlers(this IServiceCollection services) =>
        services
            .AddSingleton<CreateAlterCommandHandler>()
            .AddSingleton<UpdateAlterCommandHandler>()
            .AddSingleton<DeleteAlterCommandHandler>()
            .AddSingleton<StartFrontCommandHandler>()
            .AddSingleton<EndFrontCommandHandler>()
            .AddSingleton<BulkUpdateFrontCommandHandler>()
            .AddSingleton<SetFrontCommandHandler>()
            .AddSingleton<SetPrimaryFrontCommandHandler>()
            .AddSingleton<DeleteFrontByIdCommandHandler>()
            .AddSingleton<UpdateFrontCommentCommandHandler>()
            .AddSingleton<UpdateUsernameCommandHandler>()
            .AddSingleton<AuthenticateOAuthCommandHandler>()
            .AddSingleton<LinkOAuthIdentityCommandHandler>()
            .AddSingleton<RecordAuthTokenCommandHandler>()
            .AddSingleton<RevokeAuthTokenCommandHandler>()
            .AddSingleton<CreateLinkTokenCommandHandler>()
            .AddSingleton<UpdateDescriptionCommandHandler>()
            .AddSingleton<AddPushTokenCommandHandler>()
            .AddSingleton<RemovePushTokenCommandHandler>()
            .AddSingleton<SetupEncryptionCommandHandler>()
            .AddSingleton<RecoverEncryptionCommandHandler>()
            .AddSingleton<ResetEncryptionCommandHandler>()
            .AddSingleton<UploadAvatarCommandHandler>()
            .AddSingleton<DeleteAvatarCommandHandler>()
            .AddSingleton<ImportPkCommandHandler>()
            .AddSingleton<ImportSpCommandHandler>()
            .AddSingleton<UnlinkDiscordCommandHandler>()
            .AddSingleton<UnlinkEmailCommandHandler>()
            .AddSingleton<UnlinkAppleCommandHandler>()
            .AddSingleton<DeleteAccountCommandHandler>()
            .AddSingleton<WipeAltersCommandHandler>()
            .AddSingleton<WipeTagsCommandHandler>()
            .AddSingleton<CreateFieldCommandHandler>()
            .AddSingleton<UpdateFieldCommandHandler>()
            .AddSingleton<DeleteFieldCommandHandler>()
            .AddSingleton<RelocateFieldCommandHandler>()
            .AddSingleton<CreateTagCommandHandler>()
            .AddSingleton<UpdateTagCommandHandler>()
            .AddSingleton<DeleteTagCommandHandler>()
            .AddSingleton<AttachAlterToTagCommandHandler>()
            .AddSingleton<DetachAlterFromTagCommandHandler>()
            .AddSingleton<SetParentTagCommandHandler>()
            .AddSingleton<RemoveParentTagCommandHandler>()
            .AddSingleton<CreatePollCommandHandler>()
            .AddSingleton<UpdatePollCommandHandler>()
            .AddSingleton<DeletePollCommandHandler>()
            .AddSingleton<CreateGlobalJournalEntryCommandHandler>()
            .AddSingleton<UpdateGlobalJournalEntryCommandHandler>()
            .AddSingleton<DeleteGlobalJournalEntryCommandHandler>()
            .AddSingleton<SetGlobalJournalLockedCommandHandler>()
            .AddSingleton<SetGlobalJournalPinnedCommandHandler>()
            .AddSingleton<AttachAlterToGlobalJournalCommandHandler>()
            .AddSingleton<DetachAlterFromGlobalJournalCommandHandler>()
            .AddSingleton<CreateAlterJournalEntryCommandHandler>()
            .AddSingleton<UpdateAlterJournalEntryCommandHandler>()
            .AddSingleton<DeleteAlterJournalEntryCommandHandler>()
            .AddSingleton<SetAlterJournalLockedCommandHandler>()
            .AddSingleton<SetAlterJournalPinnedCommandHandler>()
            .AddSingleton<RemoveFriendshipCommandHandler>()
            .AddSingleton<SetFriendTrustCommandHandler>()
            .AddSingleton<SendFriendRequestCommandHandler>()
            .AddSingleton<AcceptFriendRequestCommandHandler>()
            .AddSingleton<RejectFriendRequestCommandHandler>()
            .AddSingleton<CancelFriendRequestCommandHandler>();
}

