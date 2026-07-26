using Interfold.Alters.Domain.Abstractions.Repository;
using Interfold.Fronting.Domain.Abstractions.Repository;
using Interfold.Settings.Contracts;
using Interfold.Settings.Contracts.Events;
using Interfold.Settings.Domain.Abstractions.Repository;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models.Read;
using Interfold.Shared.Domain.Abstractions.Repository;
using Interfold.Socket.Contracts;

namespace Interfold.Socket.Api.Socket.Handlers;

public static class SettingsSocketEventHandlers
{
    public static Task HandleAsync(SettingsFieldsChangedEvent evt, SocketPushContext context, ISettingsFieldRepository fieldRepository)
        => context.PushIfJoinedAsync<IReadOnlyList<SettingsFieldReadModel>, SettingsFieldsUpdatedPayload>(
            evt.TargetSystemId,
            SocketEventNames.Settings.FieldsUpdated,
            ct => fieldRepository.ListAsync(evt.TargetSystemId, ct),
            fields => new SettingsFieldsUpdatedPayload(fields));

    public static async Task HandleAsync(
        SettingsProfileUpdatedEvent evt,
        SocketPushContext context,
        IAccountRepository accountRepository,
        IAlterRepository alterRepository,
        IFrontingRepository frontingRepository,
        ISettingsFieldRepository settingsFieldRepository,
        IEncryptionStateRepository encryptionStateRepository)
    {
        if (!context.TryGetSystemTopic(evt.TargetSystemId, out var topic, out var joinRef, out var asArray))
        {
            return;
        }

        var profileTask = accountRepository.GetPublicProfileAsync(evt.TargetSystemId, context.CancellationToken);
        var altersTask = alterRepository.ListAsync(evt.TargetSystemId, context.CancellationToken);
        var frontsTask = frontingRepository.ListActiveAsync(evt.TargetSystemId, context.CancellationToken);
        var fieldsTask = settingsFieldRepository.ListAsync(evt.TargetSystemId, context.CancellationToken);
        var encryptionTask = encryptionStateRepository.GetAsync(evt.TargetSystemId, context.CancellationToken);
        await Task.WhenAll(profileTask, altersTask, frontsTask, fieldsTask, encryptionTask).ConfigureAwait(false);

        var profile = profileTask.Result;

        if (evt.EmitUsernameUpdated && profile?.Username is not null)
        {
            await context.SendAsync(topic, joinRef, asArray, SocketEventNames.Settings.UsernameUpdated, new SettingsUsernameUpdatedPayload(profile.Username.Value));
        }

        var selfData = WebSocketInitialization.BuildSelfReadModel(
            evt.TargetSystemId,
            profile,
            altersTask.Result,
            frontsTask.Result,
            fieldsTask.Result,
            encryptionTask.Result,
            context.RequestOrigin);

        await context.SendAsync(topic, joinRef, asArray, SocketEventNames.Settings.SelfUpdated, new SettingsSelfUpdatedPayload(selfData));
    }

    public static Task HandleAsync(SettingsAccountDeletedSignalEvent evt, SocketPushContext context)
        => HandleSignalAsync(evt.TargetSystemId, SocketEventNames.Settings.AccountDeleted, context);

    public static Task HandleAsync(SettingsAltersWipedSignalEvent evt, SocketPushContext context)
        => HandleSignalAsync(evt.TargetSystemId, SocketEventNames.Settings.AltersWiped, context);

    public static Task HandleAsync(SettingsEncryptedDataWipedSignalEvent evt, SocketPushContext context)
        => HandleSignalAsync(evt.TargetSystemId, SocketEventNames.Settings.EncryptedDataWiped, context);

    public static Task HandleAsync(SettingsDiscordAccountUnlinkedSignalEvent evt, SocketPushContext context)
        => HandleSignalAsync(evt.TargetSystemId, SocketEventNames.Settings.DiscordAccountUnlinked, context);

    public static Task HandleAsync(SettingsAppleAccountUnlinkedSignalEvent evt, SocketPushContext context)
        => HandleSignalAsync(evt.TargetSystemId, SocketEventNames.Settings.AppleAccountUnlinked, context);

    public static Task HandleAsync(SettingsGoogleAccountUnlinkedSignalEvent evt, SocketPushContext context)
        => HandleSignalAsync(evt.TargetSystemId, SocketEventNames.Settings.GoogleAccountUnlinked, context);

    public static Task HandleAsync(SettingsTagsWipedSignalEvent evt, SocketPushContext context)
        => HandleSignalAsync(evt.TargetSystemId, SocketEventNames.Settings.TagsWiped, context);

    private static async Task HandleSignalAsync(SystemId systemId, string eventName, SocketPushContext context)
    {
        await context.SendIfJoinedAsync(systemId, eventName, new EmptyPayload());
    }

    public static Task HandleAsync(SettingsDiscordAccountLinkedEvent evt, SocketPushContext context)
        => SendLinkedAsync(evt.TargetSystemId, SocketEventNames.Settings.DiscordAccountLinked, new DiscordAccountLinkedPayload(evt.DiscordId), context);

    public static Task HandleAsync(SettingsGoogleAccountLinkedEvent evt, SocketPushContext context)
        => SendLinkedAsync(evt.TargetSystemId, SocketEventNames.Settings.GoogleAccountLinked, new GoogleAccountLinkedPayload(evt.Email), context);

    public static Task HandleAsync(SettingsAppleAccountLinkedEvent evt, SocketPushContext context)
        => SendLinkedAsync(evt.TargetSystemId, SocketEventNames.Settings.AppleAccountLinked, new AppleAccountLinkedPayload(evt.AppleId), context);

    private static async Task SendLinkedAsync(SystemId systemId, string eventName, ISocketPayload payload, SocketPushContext context)
    {
        await context.SendIfJoinedAsync(systemId, eventName, payload);
    }
}
