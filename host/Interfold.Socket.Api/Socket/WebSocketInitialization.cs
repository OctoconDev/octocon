using System.Net.WebSockets;
using Interfold.Api.Helpers;
using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Contracts.Models.Read;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Shared.Contracts.Ids;

namespace Interfold.Api.Socket;

public class WebSocketInitialization
{
public static async Task SendBatchedInitAsync(
    WebSocket socket,
    string topic,
    string? joinReference,
    bool replyAsArrayFrame,
    SocketJoinInitPayload initPayload,
    CancellationToken cancellationToken,
    SemaphoreSlim? sendGate = null)
{
    await SendBatchedDataAsync(
        socket,
        topic,
        joinReference,
        replyAsArrayFrame,
        SocketEventNames.BatchedInit.Alters,
        3000,
        initPayload.Alters,
        (batchIndex, totalBatches, batch) => new SocketBatchedAltersPayload(batchIndex, totalBatches, batch),
        cancellationToken,
        sendGate);
    await SendBatchedDataAsync(
        socket,
        topic,
        joinReference,
        replyAsArrayFrame,
        SocketEventNames.BatchedInit.Tags,
        1000,
        initPayload.Tags,
        (batchIndex, totalBatches, batch) => new SocketBatchedTagsPayload(batchIndex, totalBatches, batch),
        cancellationToken,
        sendGate);
    await SendBatchedDataAsync(
        socket,
        topic,
        joinReference,
        replyAsArrayFrame,
        SocketEventNames.BatchedInit.Fronts,
        50,
        initPayload.Fronts,
        (batchIndex, totalBatches, batch) => new SocketBatchedFrontsPayload(batchIndex, totalBatches, batch),
        cancellationToken,
        sendGate);

    await WebSocketEvents.SendPhoenixPushAsync(
        socket,
        topic,
        joinReference,
        eventName: SocketEventNames.BatchedInit.Complete,
        payload: new EmptyPayload(),
        replyAsArrayFrame,
        cancellationToken,
        sendGate);
}

static async Task SendBatchedDataAsync<TItem, TPayload>(
    WebSocket socket,
    string topic,
    string? joinReference,
    bool replyAsArrayFrame,
    string eventName,
    int batchSize,
    IReadOnlyList<TItem> data,
    Func<int, int, IReadOnlyList<TItem>, TPayload> payloadFactory,
    CancellationToken cancellationToken,
    SemaphoreSlim? sendGate = null)
{
    if (data.Count == 0)
    {
        return;
    }

    var totalBatches = (int)Math.Ceiling((double)data.Count / batchSize);
    for (var i = 0; i < totalBatches; i++)
    {
        if (i > 0)
        {
            await Task.Delay(50, cancellationToken);
        }

        var start = i * batchSize;
        var count = Math.Min(batchSize, data.Count - start);
        var batchItems = data.Skip(start).Take(count).ToArray();

        await WebSocketEvents.SendPhoenixPushAsync(
            socket,
            topic,
            joinReference,
            eventName,
            payloadFactory(i + 1, totalBatches, batchItems),
            replyAsArrayFrame,
            cancellationToken,
            sendGate);
    }
}
     
static async Task<(
    AccountPublicProfileReadModel? profile,
    IReadOnlyList<AlterReadModel> alters,
    IReadOnlyList<FrontActiveReadModel> fronts,
    IReadOnlyList<TagReadModel> tags,
    IReadOnlyList<SettingsFieldReadModel> settingsFields,
    EncryptionState? encryptionState)>
FetchSocketInitDataAsync(
    SystemId systemId,
    IAccountRepository accounts,
    IAlterRepository alters,
    IFrontingRepository fronting,
    ITagRepository tags,
    ISettingsFieldRepository settingsFields,
    IEncryptionStateRepository encryptionStates,
    CancellationToken ct)
{
    var profileTask = accounts.GetPublicProfileAsync(systemId, ct);
    var altersTask  = alters.ListAsync(systemId, ct);
    var frontsTask  = fronting.ListActiveAsync(systemId, ct);
    var tagsTask    = tags.ListAsync(systemId, ct);
    var fieldsTask  = settingsFields.ListAsync(systemId, ct);
    var encryptionTask = encryptionStates.GetAsync(systemId, ct);
    await Task.WhenAll(profileTask, altersTask, frontsTask, tagsTask, fieldsTask, encryptionTask);
    return (profileTask.Result, altersTask.Result, frontsTask.Result, tagsTask.Result, fieldsTask.Result, encryptionTask.Result);
}

public static async Task<SocketJoinInitPayload> BuildJoinInitPayloadAsync(
    HttpContext context,
    SystemId systemId,
    CancellationToken ct)
{
    await using var scope = context.RequestServices.CreateAsyncScope();
    var sp = scope.ServiceProvider;

    var (profile, alters, fronts, tags, settingsFields, encryptionState) = await FetchSocketInitDataAsync(
        systemId,
        sp.GetRequiredService<IAccountRepository>(),
        sp.GetRequiredService<IAlterRepository>(),
        sp.GetRequiredService<IFrontingRepository>(),
        sp.GetRequiredService<ITagRepository>(),
        sp.GetRequiredService<ISettingsFieldRepository>(),
        sp.GetRequiredService<IEncryptionStateRepository>(),
        ct);

    foreach (var alter in alters)
        alter.AvatarUrl = AvatarUrlQualifier.QualifyAvatar(alter, context.Request.Scheme, context.Request.Host);

    return new SocketJoinInitPayload(
        BuildSelfReadModel(
            systemId,
            profile,
            alters,
            fronts,
            settingsFields,
            encryptionState,
            $"{context.Request.Scheme}://{context.Request.Host}"),
        alters,
        fronts,
        tags);
}

public static SocketSelfReadModel BuildSelfReadModel(
    SystemId systemId,
    AccountPublicProfileReadModel? profile,
    IReadOnlyList<AlterReadModel> alters,
    IReadOnlyList<FrontActiveReadModel> fronts,
    IReadOnlyList<SettingsFieldReadModel> settingsFields,
    EncryptionState? encryptionState,
    string? requestOrigin)
{
    var primaryFront = fronts.FirstOrDefault(x => x.Primary)?.Front.AlterId;

    return new SocketSelfReadModel(
        profile?.SystemId ?? systemId,
        profile?.Username,
        profile?.Description,
        AvatarUrlQualifier.QualifyAvatar(profile, requestOrigin),
        profile?.AvatarSource,
        AccountLinkFlag.FromValuePresence(profile?.DiscordId?.Value),
        AccountLinkFlag.NotLinked,
        AccountLinkFlag.FromValuePresence(profile?.AppleId?.Value),
        AccountLinkFlag.FromValuePresence(profile?.Email?.Value),
        AutoproxyMode.Off,
        false,
        alters.Count,
        primaryFront,
        settingsFields.OrderBy(x => x.Index).ToArray(),
        encryptionState?.Initialized ?? false);
}

}