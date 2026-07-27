using Interfold.Alters.Contracts.Abstractions;
using Interfold.Settings.Api.Services.Export;
using Interfold.Settings.Api.Services.Http;
using Interfold.Settings.Api.Services.ImportJobs;
using Interfold.Settings.Api.Services.Secrets;
using Interfold.Settings.Api.Services.SimplyPlural;
using Interfold.Settings.Contracts.Configuration;
using Interfold.Settings.Domain;
using Interfold.Settings.Domain.Abstractions;
using Interfold.Settings.Domain.Abstractions.ImportJobs;
using Interfold.Settings.Domain.Accounts;
using Interfold.Settings.Domain.Settings;
using Interfold.Shared.Contracts;
using Microsoft.Extensions.Options;

namespace Interfold.Settings.Api.DependencyInjection;

/// <summary>Settings feature module — the DI equivalent of the twenty-two settings-handler
/// <c>AddSingleton</c> calls previously living in
/// <see cref="Interfold.Infrastructure.DependencyInjection.ServiceCollectionExtensions.AddInterfoldDomainHandlers"/>,
/// plus the SimplyPlural import stack, the async-import job runners + drain loop, and the
/// two Firebase <c>IPostConfigureOptions</c> bindings that were sitting inline in
/// <c>Interfold.Api.Host/Program.cs</c>. Consumed once from
/// <c>Interfold.Api.Host/Program.cs</c> alongside the other module extensions.</summary>
public static class SettingsModuleServiceCollectionExtensions
{
    /// <summary>Registers everything the Settings feature owns: twenty-two command-handler
    /// singletons (account / encryption / avatar / import-dispatch / unlink / delete-account /
    /// wipe-alters / wipe-tags / field CRUD), the two <see cref="IImportJobRunner"/>
    /// implementations (<c>PkImportJobRunner</c> and <c>SpImportJobRunner</c>),
    /// <see cref="ImportJobBackgroundService"/> as an <see cref="IHostedService"/> drain loop,
    /// <see cref="ISimplyPluralImportService"/> plus its named
    /// <see cref="System.Net.Http.HttpClient"/> (with the shared
    /// <see cref="HttpLoggingHandler"/> handler-chain), and the two
    /// <see cref="IPostConfigureOptions{TOptions}"/> bindings for
    /// <see cref="FirebaseClientConfiguration"/> + <see cref="FcmConfiguration"/>. The
    /// <see cref="IImportJobQueue"/> registration stays inside
    /// <c>AddInterfoldCluster</c> (infrastructure-owned single-channel implementation).</summary>
    public static IServiceCollection AddSettingsModule(this IServiceCollection services)
    {
        services
            .AddSingleton<UpdateUsernameCommandHandler>()
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
            .AddSingleton<RelocateFieldCommandHandler>();

        services.AddSingleton<IPostConfigureOptions<FirebaseClientConfiguration>, FirebaseClientSecretsPostConfigure>();
        services.AddSingleton<IPostConfigureOptions<FcmConfiguration>, FcmSecretsPostConfigure>();

        services.AddTransient<HttpLoggingHandler>();
        services.AddHttpClient(HttpClientNames.SimplyPlural).AddHttpMessageHandler<HttpLoggingHandler>();
        services.AddSingleton<ISimplyPluralImportService, SimplyPluralImportService>();
        services.AddSingleton<IImportJobRunner, SpImportJobRunner>();
        services.AddSingleton<IImportJobRunner, PkImportJobRunner>();
        services.AddHostedService<ImportJobBackgroundService>();
        services.AddSingleton<IExportService, ExportService>();
        services.AddSingleton<IAlterFieldDefinitions, AlterFieldDefinitionsAdapter>();

        return services;
    }
}
