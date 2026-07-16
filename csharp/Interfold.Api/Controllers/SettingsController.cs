using Microsoft.AspNetCore.Mvc;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Interfold.Api.Helpers;
using Interfold.Api.Models;
using Interfold.Api.Services;
using Interfold.Contracts;
using Interfold.Contracts.Configuration;
using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Models.Read;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Domain.Accounts;
using Interfold.Domain.Settings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Interfold.Api.Controllers.Base;
using System.Net;

namespace Interfold.Api.Controllers;

[Route("api/settings")]
public sealed class SettingsController : InterfoldControllerBase
{
    private readonly IAccountRepository _accountRepository;
    private readonly ISingletonTaskOwner _singletonTaskOwner;
    private readonly IAvatarStorage _avatarStorage;
    private readonly IOptionsMonitor<AuthenticationConfiguration> _authenticationConfiguration;
    private readonly IOptionsMonitor<FirebaseClientConfiguration> _firebaseClientConfiguration;

    private readonly UpdateUsernameCommandHandler _usernameHandler;
    private readonly UpdateDescriptionCommandHandler _descriptionHandler;
    private readonly AddPushTokenCommandHandler _addPushTokenHandler;
    private readonly RemovePushTokenCommandHandler _removePushTokenHandler;
    private readonly SetupEncryptionCommandHandler _setupEncryptionHandler;
    private readonly RecoverEncryptionCommandHandler _recoverEncryptionHandler;
    private readonly ResetEncryptionCommandHandler _resetEncryptionHandler;
    private readonly UploadAvatarCommandHandler _uploadAvatarHandler;
    private readonly DeleteAvatarCommandHandler _deleteAvatarHandler;
    private readonly ImportPkCommandHandler _importPkHandler;
    private readonly ImportSpCommandHandler _importSpHandler;
    private readonly UnlinkDiscordCommandHandler _unlinkDiscordHandler;
    private readonly UnlinkEmailCommandHandler _unlinkEmailHandler;
    private readonly UnlinkAppleCommandHandler _unlinkAppleHandler;
    private readonly DeleteAccountCommandHandler _deleteAccountHandler;
    private readonly WipeAltersCommandHandler _wipeAltersHandler;
    private readonly WipeTagsCommandHandler _wipeTagsHandler;
    private readonly CreateFieldCommandHandler _createFieldHandler;
    private readonly UpdateFieldCommandHandler _updateFieldHandler;
    private readonly DeleteFieldCommandHandler _deleteFieldHandler;
    private readonly RelocateFieldCommandHandler _relocateFieldHandler;

    public SettingsController(
        IAccountRepository accountRepository,
        ISingletonTaskOwner singletonTaskOwner,
        UpdateUsernameCommandHandler usernameHandler,
        UpdateDescriptionCommandHandler descriptionHandler,
        AddPushTokenCommandHandler addPushTokenHandler,
        RemovePushTokenCommandHandler removePushTokenHandler,
        SetupEncryptionCommandHandler setupEncryptionHandler,
        RecoverEncryptionCommandHandler recoverEncryptionHandler,
        ResetEncryptionCommandHandler resetEncryptionHandler,
        UploadAvatarCommandHandler uploadAvatarHandler,
        IAvatarStorage avatarStorage,
        DeleteAvatarCommandHandler deleteAvatarHandler,
        ImportPkCommandHandler importPkHandler,
        ImportSpCommandHandler importSpHandler,
        UnlinkDiscordCommandHandler unlinkDiscordHandler,
        UnlinkEmailCommandHandler unlinkEmailHandler,
        UnlinkAppleCommandHandler unlinkAppleHandler,
        DeleteAccountCommandHandler deleteAccountHandler,
        WipeAltersCommandHandler wipeAltersHandler,
        WipeTagsCommandHandler wipeTagsHandler,
        CreateFieldCommandHandler createFieldHandler,
        UpdateFieldCommandHandler updateFieldHandler,
        DeleteFieldCommandHandler deleteFieldHandler,
        RelocateFieldCommandHandler relocateFieldHandler,
        IOptionsMonitor<AuthenticationConfiguration> authenticationConfiguration,
        IOptionsMonitor<FirebaseClientConfiguration> firebaseClientConfiguration)
    {
        _accountRepository = accountRepository;
        _singletonTaskOwner = singletonTaskOwner;
        _usernameHandler = usernameHandler;
        _descriptionHandler = descriptionHandler;
        _addPushTokenHandler = addPushTokenHandler;
        _removePushTokenHandler = removePushTokenHandler;
        _setupEncryptionHandler = setupEncryptionHandler;
        _recoverEncryptionHandler = recoverEncryptionHandler;
        _resetEncryptionHandler = resetEncryptionHandler;
        _uploadAvatarHandler = uploadAvatarHandler;
        _avatarStorage = avatarStorage;
        _deleteAvatarHandler = deleteAvatarHandler;
        _importPkHandler = importPkHandler;
        _importSpHandler = importSpHandler;
        _unlinkDiscordHandler = unlinkDiscordHandler;
        _unlinkEmailHandler = unlinkEmailHandler;
        _unlinkAppleHandler = unlinkAppleHandler;
        _deleteAccountHandler = deleteAccountHandler;
        _wipeAltersHandler = wipeAltersHandler;
        _wipeTagsHandler = wipeTagsHandler;
        _createFieldHandler = createFieldHandler;
        _updateFieldHandler = updateFieldHandler;
        _deleteFieldHandler = deleteFieldHandler;
        _relocateFieldHandler = relocateFieldHandler;
        _authenticationConfiguration = authenticationConfiguration;
        _firebaseClientConfiguration = firebaseClientConfiguration;
    }

    [HttpGet("link_token")]
    public async Task<IActionResult> GetLinkToken(CancellationToken ct)
    {
        // Token creation (write) is gated to primary nodes only — mirrors
        // Octocon.Global.LinkTokenRegistry singleton in the legacy Elixir runtime.
        // On auxiliary/sidecar nodes attempt a read-only lookup; if no token has
        // been provisioned yet, return 503 to inform the caller to retry via a
        // primary node.
        var principal = PrincipalId;
        if (_singletonTaskOwner.OwnsTask(SingletonTaskNames.LinkTokenRegistry))
        {
            var token = await _accountRepository.GetOrCreateLinkTokenAsync(principal, ct);
            return Ok(new SuccessResponse<LinkTokenReadModel>(new LinkTokenReadModel(token)));
        }

        var existing = await _accountRepository.GetLinkTokenAsync(principal, ct);
        if (existing is null)
        {
            // 503 shape is intentionally non-standard (has `hint` instead of `code`) — the
            // Kotlin client and legacy Elixir server both use this shape, so the typed
            // record preserves it byte-for-byte.
            return StatusCode(503, new LinkTokenUnavailableResponse("link_token_unavailable", "Retry on a primary node."));
        }

        return Ok(new SuccessResponse<LinkTokenReadModel>(new LinkTokenReadModel(existing.Value)));
    }

    [HttpPost("username")]
    public async Task<Response> UpdateUsername([FromBody] SettingsUsernameRequest req, CancellationToken ct)
    {
        var principal = PrincipalId;
        var envelope = new CommandEnvelope<UpdateUsernameCommand>(
            OperationIds.SettingsUsernameUpdate, Guid.NewGuid(),
            PrincipalId: principal,
            IdempotencyKey: GetIdempotencyKey(),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new UpdateUsernameCommand(req.Username)
        );

        return CommandNoContent(await _usernameHandler.HandleAsync(envelope, ct));
    }

    [HttpPost("description")]
    public async Task<Response> UpdateDescription([FromBody] SettingsDescriptionRequest req, CancellationToken ct)
    {
        var envelope = new CommandEnvelope<UpdateDescriptionCommand>(
            OperationIds.SettingsDescriptionUpdate,
            Guid.NewGuid(),
            PrincipalId: PrincipalId,
            IdempotencyKey: GetIdempotencyKey(),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new UpdateDescriptionCommand(req.Description)
        );

        return CommandNoContent(await _descriptionHandler.HandleAsync(envelope, ct));
    }

    [HttpPost("push-token")]
    public async Task<Response> AddPushToken([FromBody] SettingsPushTokenRequest req, CancellationToken ct)
    {
        if (req.Token is not { } pushToken || string.IsNullOrWhiteSpace(pushToken.Value))
            return new ErrorResponse("Invalid push token.", ErrorCodes.InvalidPushToken, System.Net.HttpStatusCode.BadRequest);

        var envelope = new CommandEnvelope<AddPushTokenCommand>(
            OperationIds.SettingsPushTokenAdd,
            Guid.NewGuid(),
            PrincipalId: PrincipalId,
            IdempotencyKey: GetIdempotencyKey(),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new AddPushTokenCommand(pushToken)
        );

        return CommandNoContent(await _addPushTokenHandler.HandleAsync(envelope, ct));
    }

    [HttpDelete("push-token")]
    public async Task<Response> RemovePushToken([FromBody] SettingsPushTokenRequest req, CancellationToken ct)
    {
        if (req.Token is not { } pushToken || string.IsNullOrWhiteSpace(pushToken.Value))
            return new ErrorResponse("Invalid push token.", ErrorCodes.InvalidPushToken, System.Net.HttpStatusCode.BadRequest);

        var envelope = new CommandEnvelope<RemovePushTokenCommand>(
            OperationIds.SettingsPushTokenRemove,
            Guid.NewGuid(),
            PrincipalId: PrincipalId,
            IdempotencyKey: GetIdempotencyKey(),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new RemovePushTokenCommand(pushToken)
        );

        return CommandNoContent(await _removePushTokenHandler.HandleAsync(envelope, ct));
    }

    [HttpPost("setup-encryption")]
    public async Task<Response<EncryptionKeyResponse>> SetupEncryption([FromBody] SettingsEncryptionRequest req, CancellationToken ct)
    {
        if (!TryResolveRecoveryCode(req.RecoveryCode.Value, out var recoveryCode, out var decryptionErrorCode))
            return new ErrorResponse("Failed to decrypt recovery code.", decryptionErrorCode, System.Net.HttpStatusCode.BadRequest);

        var envelope = new CommandEnvelope<SetupEncryptionCommand>(
            OperationIds.SettingsEncryptionSetup,
            Guid.NewGuid(),
            PrincipalId: PrincipalId,
            IdempotencyKey: GetIdempotencyKey(),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new SetupEncryptionCommand(recoveryCode)
        );

        var execution = await _setupEncryptionHandler.HandleAsync(envelope, ct);
        if (execution.Accepted)
            return new SuccessResponse<EncryptionKeyResponse>(new EncryptionKeyResponse(Convert.ToBase64String(Encoding.UTF8.GetBytes(execution.Result!.Key.Value))));

        return ConflictToError(execution.Conflict!);
    }

    [HttpPost("recover-encryption")]
    public async Task<Response<EncryptionKeyResponse>> RecoverEncryption([FromBody] SettingsEncryptionRequest req, CancellationToken ct)
    {
        if (!TryResolveRecoveryCode(req.RecoveryCode.Value, out var recoveryCode, out var decryptionErrorCode))
            return new ErrorResponse("Failed to decrypt recovery code.", decryptionErrorCode, System.Net.HttpStatusCode.BadRequest);

        var envelope = new CommandEnvelope<RecoverEncryptionCommand>(
            OperationIds.SettingsEncryptionRecover,
            Guid.NewGuid(),
            PrincipalId: PrincipalId,
            IdempotencyKey: GetIdempotencyKey(),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new RecoverEncryptionCommand(recoveryCode)
        );

        var execution = await _recoverEncryptionHandler.HandleAsync(envelope, ct);
        if (execution.Accepted)
            return new SuccessResponse<EncryptionKeyResponse>(new EncryptionKeyResponse(Convert.ToBase64String(Encoding.UTF8.GetBytes(execution.Result!.Key.Value))));

        //TODO: To ensure route works as expected
        return ConflictToError(execution.Conflict!);
    }

    //TODO: To ensure route works as expected - does not delete journal entries currently which need adding
    [HttpPost("reset-encryption")]
    public async Task<Response> ResetEncryption(CancellationToken ct)
    {
        var envelope = new CommandEnvelope<ResetEncryptionCommand>(
            OperationIds.SettingsEncryptionReset,
            Guid.NewGuid(),
            PrincipalId: PrincipalId,
            IdempotencyKey: GetIdempotencyKey(),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new ResetEncryptionCommand()
        );

        return CommandNoContent(await _resetEncryptionHandler.HandleAsync(envelope, ct));
    }

    [HttpPut("avatar")]
    [Consumes("multipart/form-data")]
    public async Task<Response> UploadAvatarMultipart(CancellationToken ct)
    {
        var principal = PrincipalId;
        var upload = await ResolveMultipartUploadAsync(ct);
        var avatarStream = upload.Stream;
        if (avatarStream is null)
        {
            if (upload.EmptyFilePart)
                return new ErrorResponse("Avatar file is empty.", ErrorCodes.AvatarFileEmpty, System.Net.HttpStatusCode.BadRequest);

            return new ErrorResponse("No avatar file provided.", ErrorCodes.AvatarFileRequired, System.Net.HttpStatusCode.BadRequest);
        }

        AvatarUrl avatarUrl;
        try
        {
            await using (avatarStream)
            {
                avatarUrl = await _avatarStorage.SaveSystemAvatarAsync(principal, avatarStream, ct);
            }
        }
        catch
        {
            return new ErrorResponse("An error occurred while uploading the file.", ErrorCodes.UnknownError, System.Net.HttpStatusCode.InternalServerError);
        }

        AvatarUrl? currentAvatarUrl = null;
        AvatarSource? currentAvatarSource = null;
        try
        {
            var currentProfile = await _accountRepository.GetPublicProfileAsync(principal, ct);
            currentAvatarUrl = currentProfile?.AvatarUrl;
            currentAvatarSource = currentProfile?.AvatarSource;
        }
        catch
        {
        }

        var envelope = new CommandEnvelope<UploadAvatarCommand>(
            OperationIds.SettingsAvatarUpload,
            Guid.NewGuid(),
            PrincipalId: principal,
            IdempotencyKey: GetIdempotencyKey(),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new UploadAvatarCommand(avatarUrl, AvatarSource.Local)
        );

        var result = CommandNoContent(await _uploadAvatarHandler.HandleAsync(envelope, ct));

        if (!result.IsSuccess)
        {
            return result;
        }

        // Only the local storage owns the previous bytes; an external URL was never ours
        // to delete and must be passed through untouched.
        if (currentAvatarSource == AvatarSource.Local)
        {
            try
            {
                await _avatarStorage.DeleteByUrlAsync(currentAvatarUrl, ct);
            }
            catch
            {
                // Avatar metadata was updated successfully; tolerate storage cleanup failures.
            }
        }

        return result;
    }

    /// <summary>
    /// Sibling action that consumes <c>application/json</c> on the same route + verb.
    /// ASP.NET Core's <see cref="ConsumesAttribute"/> is an <c>IActionConstraint</c>, so it
    /// dispatches purely on Content-Type and leaves the multipart action above untouched.
    /// </summary>
    [HttpPut("avatar")]
    [Consumes("application/json")]
    public async Task<Response> UploadAvatarByUrl([FromBody] AvatarUrlUploadRequest req, CancellationToken ct)
    {
        if (req is null)
            return new ErrorResponse("Avatar URL payload required.", ErrorCodes.AvatarUrlInvalid, System.Net.HttpStatusCode.BadRequest);

        if (!AvatarUrlValidator.TryNormalize(req.Url.Value, out var url, out var err))
            return new ErrorResponse("Invalid avatar URL.", err, System.Net.HttpStatusCode.BadRequest);

        var principal = PrincipalId;

        AvatarUrl? currentAvatarUrl = null;
        AvatarSource? currentAvatarSource = null;
        try
        {
            var currentProfile = await _accountRepository.GetPublicProfileAsync(principal, ct);
            currentAvatarUrl = currentProfile?.AvatarUrl;
            currentAvatarSource = currentProfile?.AvatarSource;
        }
        catch
        {
        }

        var envelope = new CommandEnvelope<UploadAvatarCommand>(
            OperationIds.SettingsAvatarUpload,
            Guid.NewGuid(),
            PrincipalId: principal,
            IdempotencyKey: GetIdempotencyKey(),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new UploadAvatarCommand(new(url), AvatarSource.External)
        );

        var result = CommandNoContent(await _uploadAvatarHandler.HandleAsync(envelope, ct));

        if (!result.IsSuccess)
        {
            return result;
        }

        if (currentAvatarSource == AvatarSource.Local)
        {
            try
            {
                await _avatarStorage.DeleteByUrlAsync(currentAvatarUrl, ct);
            }
            catch
            {
            }
        }

        return result;
    }

    [HttpDelete("avatar")]
    public async Task<Response> DeleteAvatar(CancellationToken ct)
    {
        var principal = PrincipalId;
        var currentProfile = await _accountRepository.GetPublicProfileAsync(principal, ct);
        AvatarUrl? currentAvatarUrl = currentProfile?.AvatarUrl;
        var currentAvatarSource = currentProfile?.AvatarSource;

        var envelope = new CommandEnvelope<DeleteAvatarCommand>(
            OperationIds.SettingsAvatarDelete,
            Guid.NewGuid(),
            PrincipalId: principal,
            IdempotencyKey: GetIdempotencyKey(),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new DeleteAvatarCommand()
        );

        var execution = await _deleteAvatarHandler.HandleAsync(envelope, ct);
        if (execution.Accepted && currentAvatarSource == AvatarSource.Local)
        {
            try
            {
                await _avatarStorage.DeleteByUrlAsync(currentAvatarUrl, ct);
            }
            catch
            {
                // Avatar metadata was cleared successfully; tolerate storage cleanup failures.
            }
        }

        return CommandNoContent(execution);
    }

    /// <summary>
    /// Dispatches a PluralKit import onto the background worker. Returns 202 Accepted
    /// with the dispatched operation_id; the actual import outcome is pushed over the
    /// WebSocket as <c>pk_import_complete</c> or <c>pk_import_failed</c>. Concurrent
    /// dispatches for the same system collapse onto the in-flight operation rather
    /// than starting a second importer run.
    /// </summary>
    [HttpPost("import-pk")]
    public async Task<Response<ImportDispatchResponse>> ImportPk([FromBody] SettingsImportRequest req, CancellationToken ct)
    {
        var envelope = new CommandEnvelope<ImportPkCommand>(
            OperationIds.SettingsImportPk,
            Guid.NewGuid(),
            PrincipalId: PrincipalId,
            IdempotencyKey: GetIdempotencyKey(),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new ImportPkCommand(req.Token)
        );

        return CommandAccepted(
            await _importPkHandler.HandleAsync(envelope, ct),
            r => new ImportDispatchResponse(r.OperationId, r.Status, r.StartedAt));
    }

    /// <summary>
    /// Dispatches a Simply Plural import onto the background worker. Returns 202 Accepted
    /// with the dispatched operation_id in &lt;500 ms (no synchronous import work runs
    /// here). The actual import outcome — including the per-import alter count for
    /// successful runs — is pushed over the WebSocket as <c>sp_import_complete</c> or
    /// <c>sp_import_failed</c> and the frontend already listens for those frames.
    /// Concurrent dispatches for the same system collapse onto the existing in-flight
    /// operation via the Cassandra LWT mutex on <c>active_import_by_system</c>, so a
    /// browser retry, Polly retry, or double-clicked button can no longer trigger a
    /// duplicate import run.
    /// </summary>
    [HttpPost("import-sp")]
    public async Task<Response<ImportDispatchResponse>> ImportSp([FromBody] SettingsImportRequest req, CancellationToken ct)
    {
        // recoveryCode is nullable at this layer, but the resolver's out param is a
        // non-nullable RecoveryCode — we can't write it directly into the nullable outer
        // local, so the guard splits into a nested-if. Splitting it also makes the
        // "code supplied but blank" case a clean no-op: nothing to decrypt, nothing to
        // return an error for.
        RecoveryCode? recoveryCode = null;
        if (req.RecoveryCode is { } suppliedRecoveryCode
            && !string.IsNullOrWhiteSpace(suppliedRecoveryCode.Value))
        {
            if (!TryResolveRecoveryCode(suppliedRecoveryCode.Value, out var resolved, out var decryptionErrorCode))
                return new ErrorResponse("Failed to decrypt recovery code.", decryptionErrorCode, System.Net.HttpStatusCode.BadRequest);

            recoveryCode = resolved;
        }

        var envelope = new CommandEnvelope<ImportSpCommand>(
            OperationIds.SettingsImportSp,
            Guid.NewGuid(),
            PrincipalId: PrincipalId,
            IdempotencyKey: GetIdempotencyKey(),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new ImportSpCommand(req.Token, recoveryCode)
        );

        return CommandAccepted(
            await _importSpHandler.HandleAsync(envelope, ct),
            r => new ImportDispatchResponse(r.OperationId, r.Status, r.StartedAt));
    }

    [HttpPost("unlink_discord")]
    public async Task<Response> UnlinkDiscord(CancellationToken ct)
    {
        var envelope = new CommandEnvelope<UnlinkDiscordCommand>(
            OperationIds.SettingsAuthUnlinkDiscord,
            Guid.NewGuid(),
            PrincipalId: PrincipalId,
            IdempotencyKey: GetIdempotencyKey(),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new UnlinkDiscordCommand()
        );

        return CommandNoContent(await _unlinkDiscordHandler.HandleAsync(envelope, ct));
    }

    [HttpPost("unlink_email")]
    public async Task<Response> UnlinkEmail(CancellationToken ct)
    {
        var envelope = new CommandEnvelope<UnlinkEmailCommand>(
            OperationIds.SettingsAuthUnlinkEmail,
            Guid.NewGuid(),
            PrincipalId: PrincipalId,
            IdempotencyKey: GetIdempotencyKey(),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new UnlinkEmailCommand()
        );

        return CommandNoContent(await _unlinkEmailHandler.HandleAsync(envelope, ct));
    }

    //TODO: To ensure route works as expected - other ones work but this one needs testing to ensure the command handler is correctly implemented
    [HttpPost("unlink_apple")]
    public async Task<Response> UnlinkApple(CancellationToken ct)
    {
        var envelope = new CommandEnvelope<UnlinkAppleCommand>(
            OperationIds.SettingsAuthUnlinkApple,
            Guid.NewGuid(),
            PrincipalId: PrincipalId,
            IdempotencyKey: GetIdempotencyKey(),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new UnlinkAppleCommand()
        );

        return CommandNoContent(await _unlinkAppleHandler.HandleAsync(envelope, ct));
    }

    //TODO: To ensure route works as expected
    [HttpPost("delete-account")]
    public async Task<Response> DeleteAccount(CancellationToken ct)
    {
        var envelope = new CommandEnvelope<DeleteAccountCommand>(
            OperationIds.SettingsAccountDelete,
            Guid.NewGuid(),
            PrincipalId: PrincipalId,
            IdempotencyKey: GetIdempotencyKey(),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new DeleteAccountCommand()
        );

        return CommandNoContent(await _deleteAccountHandler.HandleAsync(envelope, ct));
    }

    [HttpPost("wipe-alters")]
    public async Task<Response> WipeAlters(CancellationToken ct)
    {
        var envelope = new CommandEnvelope<WipeAltersCommand>(
            OperationIds.SettingsAltersWipe,
            Guid.NewGuid(),
            PrincipalId: PrincipalId,
            IdempotencyKey: GetIdempotencyKey(),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new WipeAltersCommand()
        );

        return CommandNoContent(await _wipeAltersHandler.HandleAsync(envelope, ct));
    }

    [HttpPost("wipe-tags")]
    public async Task<Response> WipeTags(CancellationToken ct)
    {
        var envelope = new CommandEnvelope<WipeTagsCommand>(
            OperationIds.SettingsTagsWipe,
            Guid.NewGuid(),
            PrincipalId: PrincipalId,
            IdempotencyKey: GetIdempotencyKey(),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new WipeTagsCommand()
        );

        return CommandNoContent(await _wipeTagsHandler.HandleAsync(envelope, ct));
    }

    [HttpPost("fields")]
    public async Task<Response<FieldCreatedResponse>> CreateField([FromBody] SettingsCreateFieldRequest req, CancellationToken ct)
    {
        // CreateFieldCommandHandler owns InsertedAtUtc on the public-API path and derives it
        // from the envelope's OccurredAt right before calling the repo. Passing `default`
        // here keeps the hashed payload stable across retries with the same idempotency key
        // (otherwise every call would stamp a fresh DateTime.UtcNow and look like a
        // different request, triggering ConflictDuplicate on every replay).
        var envelope = new CommandEnvelope<CreateFieldCommand>(
            OperationIds.SettingsFieldCreate,
            Guid.NewGuid(),
            PrincipalId: PrincipalId,
            IdempotencyKey: GetIdempotencyKey(),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new CreateFieldCommand(req.Name, req.Type ?? FieldType.Text, req.SecurityLevel ?? VisibilityLevel.Private, req.Locked ?? false, InsertedAtUtc: default)
        );

        var execution = await _createFieldHandler.HandleAsync(envelope, ct);
        if (!execution.Accepted)
            return ConflictToError(execution.Conflict!);

        return new SuccessResponse<FieldCreatedResponse>(new FieldCreatedResponse(execution.Result!.FieldId), System.Net.HttpStatusCode.Created, execution.Result.Replay);
    }

    [HttpPatch("fields/{id}")]
    public async Task<Response> UpdateField([FromRoute] FieldId id, [FromBody] SettingsUpdateFieldRequest req, CancellationToken ct)
    {
        var envelope = new CommandEnvelope<UpdateFieldCommand>(
            OperationIds.SettingsFieldUpdate,
            Guid.NewGuid(),
            PrincipalId: PrincipalId,
            IdempotencyKey: GetIdempotencyKey(),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new UpdateFieldCommand(id, req.Name, req.SecurityLevel, req.Locked)
        );

        return CommandNoContent(await _updateFieldHandler.HandleAsync(envelope, ct));
    }

    [HttpDelete("fields/{id}")]
    public async Task<Response> DeleteField([FromRoute] FieldId id, CancellationToken ct)
    {
        var envelope = new CommandEnvelope<DeleteFieldCommand>(
            OperationIds.SettingsFieldDelete,
            Guid.NewGuid(),
            PrincipalId: PrincipalId,
            IdempotencyKey: GetIdempotencyKey(),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new DeleteFieldCommand(id)
        );

        return CommandNoContent(await _deleteFieldHandler.HandleAsync(envelope, ct));
    }

    [HttpPost("fields/{id}/relocate")]
    public async Task<Response> RelocateField([FromRoute] FieldId id, [FromBody] SettingsRelocateFieldRequest req, CancellationToken ct)
    {
        var envelope = new CommandEnvelope<RelocateFieldCommand>(
            OperationIds.SettingsFieldRelocate,
            Guid.NewGuid(),
            PrincipalId: PrincipalId,
            IdempotencyKey: GetIdempotencyKey(),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new RelocateFieldCommand(id, req.Index)
        );

        return CommandNoContent(await _relocateFieldHandler.HandleAsync(envelope, ct));
    }

    /* New endpoints from here! */
    [HttpGet("public-key")]
    [AllowAnonymous]
    public async Task<Response<string>> GetPublicKey(CancellationToken ct)
    {
        var authenticationConfiguration = _authenticationConfiguration.CurrentValue;
        return authenticationConfiguration.Rsa256PublicKey;
    }

    /// <summary>
    /// Serves the per-platform Firebase client-init payload the mobile / wasm apps fetch
    /// at runtime. Values are PUBLIC (they previously shipped inside every distributed
    /// bundle as <c>google-services.json</c> / <c>GoogleService-Info.plist</c> / hardcoded
    /// web config), so the endpoint is anonymous — mirrors the <c>public-key</c> shape
    /// above.
    /// <para>
    /// Contract:
    /// <list type="bullet">
    ///   <item><c>?platform=android|ios|web</c> — required. Unknown / missing values return 400 with code <c>invalid_platform</c>.</item>
    ///   <item>Configured platform absent from <c>internal.secrets</c> — 503 <c>firebase_config_unavailable</c>. The deployment hasn't seeded the matching row via the bootstrapper's Firebase phase.</item>
    ///   <item>Cache headers: <c>Cache-Control: public, max-age=300, must-revalidate</c> plus a body-hash <c>ETag</c>. The client keeps a 7-day cache (see <c>FirebaseConfigProvider</c>); the server ceiling of 5 minutes bounds how long a rotated config takes to reach the service-worker install path.</item>
    /// </list>
    /// </para>
    /// </summary>
    [HttpGet("firebase-config")]
    [AllowAnonymous]
    public async Task<Response<FirebaseClientConfigResponse>> GetFirebaseConfig(
        [FromQuery] string? platform,
        CancellationToken ct)
    {
        var (payload, error) = FirebaseConfigResolver.Resolve(_firebaseClientConfiguration.CurrentValue, platform);
        if (error is not null)
            return error;

        FirebaseConfigResolver.ApplyCacheHeaders(Response, payload!);
        return new SuccessResponse<FirebaseClientConfigResponse>(payload!);
    }

    private bool TryResolveRecoveryCode(string candidate, out RecoveryCode recoveryCode, out ErrorCode errorCode)
        => Helpers.RecoveryCodeResolver.TryResolve(candidate, _authenticationConfiguration.CurrentValue.Rsa256PrivateKey, out recoveryCode, out errorCode);
}
