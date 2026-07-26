using Microsoft.AspNetCore.Mvc;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Interfold.Api.Helpers;
using Interfold.Api.Models;
using Interfold.Api.Services;
using Interfold.Api.Services.Export;
using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Configuration;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Contracts.Models.Commands;
using Interfold.Shared.Contracts.Models.Read;
using Interfold.Shared.Contracts.Operations;
using Interfold.Shared.Domain.Abstractions;
using Interfold.Shared.Domain.Abstractions.Repository;
using Interfold.Shared.Domain.Accounts;
using Interfold.Shared.Domain.Settings;
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
    private readonly CreateLinkTokenCommandHandler _createLinkTokenHandler;
    private readonly IExportService _exportService;

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
        IOptionsMonitor<FirebaseClientConfiguration> firebaseClientConfiguration,
        CreateLinkTokenCommandHandler createLinkTokenHandler,
        IExportService exportService)
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
        _createLinkTokenHandler = createLinkTokenHandler;
        _authenticationConfiguration = authenticationConfiguration;
        _firebaseClientConfiguration = firebaseClientConfiguration;
        _exportService = exportService;
    }

    /// <summary>
    /// Downloads the authenticated user's export in either PluralKit v2 importable form
    /// (<c>?format=pk</c>) or the full-fidelity Octocon backup (<c>?format=full</c>).
    /// </summary>
    /// <remarks>
    /// Response body is the raw JSON payload at the root, allowing PluralKit users to save the file and feed it directly to PK's
    /// importer. Missing or unrecognised <c>?format</c> returns 400 with
    /// <see cref="ErrorCodes.InvalidExportFormat"/>.
    /// </remarks>
    [HttpGet("export")]
    public async Task<IActionResult> Export([FromQuery] ExportFormat? format, CancellationToken ct)
    {
        if (format is not { } fmt)
        {
            return BadRequest(new ErrorResponse("Invalid export format.", ErrorCodes.InvalidExportFormat, HttpStatusCode.BadRequest));
        }

        var payload = fmt switch
        {
            ExportFormat.Pk => (object)await _exportService.BuildPkAsync(PrincipalId, ct),
            ExportFormat.Full => await _exportService.BuildFullAsync(PrincipalId, ct),
            _ => throw new InvalidOperationException($"Unhandled ExportFormat: {fmt}"),
        };

        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, payload.GetType(), ExportJsonOptions.Default);
        var wire = EnumWire<ExportFormat>.ToWire(fmt);
        return File(bytes, "application/json", $"octocon_export_{wire}.json");
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
            var result = await _createLinkTokenHandler.HandleAsync(BuildEnvelope(OperationIds.SettingsLinkToken, new CreateLinkTokenCommand()), ct);
            return Ok(new SuccessResponse<LinkTokenReadModel>(new LinkTokenReadModel(result.Result!.Token)));
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
        return await DispatchNoContentAsync(_usernameHandler, OperationIds.SettingsUsernameUpdate, new UpdateUsernameCommand(req.Username)
        , ct);
    }

    [HttpPost("description")]
    public async Task<Response> UpdateDescription([FromBody] SettingsDescriptionRequest req, CancellationToken ct)
    {
        return await DispatchNoContentAsync(_descriptionHandler, OperationIds.SettingsDescriptionUpdate, new UpdateDescriptionCommand(req.Description)
        , ct);
    }

    [HttpPost("push-token")]
    public async Task<Response> AddPushToken([FromBody] SettingsPushTokenRequest req, CancellationToken ct)
    {
        if (req.Token is not { } pushToken || string.IsNullOrWhiteSpace(pushToken.Value))
            return new ErrorResponse("Invalid push token.", ErrorCodes.InvalidPushToken, System.Net.HttpStatusCode.BadRequest);

        return await DispatchNoContentAsync(_addPushTokenHandler, OperationIds.SettingsPushTokenAdd, new AddPushTokenCommand(pushToken)
        , ct);
    }

    [HttpDelete("push-token")]
    public async Task<Response> RemovePushToken([FromBody] SettingsPushTokenRequest req, CancellationToken ct)
    {
        if (req.Token is not { } pushToken || string.IsNullOrWhiteSpace(pushToken.Value))
            return new ErrorResponse("Invalid push token.", ErrorCodes.InvalidPushToken, System.Net.HttpStatusCode.BadRequest);

        return await DispatchNoContentAsync(_removePushTokenHandler, OperationIds.SettingsPushTokenRemove, new RemovePushTokenCommand(pushToken)
        , ct);
    }

    [HttpPost("setup-encryption")]
    public async Task<Response<EncryptionKeyResponse>> SetupEncryption([FromBody] SettingsEncryptionRequest req, CancellationToken ct)
    {
        if (!TryResolveRecoveryCode(req.RecoveryCode.Value, out var recoveryCode, out var decryptionErrorCode))
            return new ErrorResponse("Failed to decrypt recovery code.", decryptionErrorCode, System.Net.HttpStatusCode.BadRequest);

        return await DispatchOkAsync(
            _setupEncryptionHandler,
            OperationIds.SettingsEncryptionSetup,
            new SetupEncryptionCommand(recoveryCode),
            ToEncryptionKeyResponse,
            ct);
    }

    [HttpPost("recover-encryption")]
    public async Task<Response<EncryptionKeyResponse>> RecoverEncryption([FromBody] SettingsEncryptionRequest req, CancellationToken ct)
    {
        if (!TryResolveRecoveryCode(req.RecoveryCode.Value, out var recoveryCode, out var decryptionErrorCode))
            return new ErrorResponse("Failed to decrypt recovery code.", decryptionErrorCode, System.Net.HttpStatusCode.BadRequest);

        return await DispatchOkAsync(
            _recoverEncryptionHandler,
            OperationIds.SettingsEncryptionRecover,
            new RecoverEncryptionCommand(recoveryCode),
            ToEncryptionKeyResponse,
            ct);
    }

    [HttpPost("reset-encryption")]
    public async Task<Response> ResetEncryption(CancellationToken ct)
    {
        return await DispatchNoContentAsync(_resetEncryptionHandler, OperationIds.SettingsEncryptionReset, new ResetEncryptionCommand()
        , ct);
    }

    [HttpPut("avatar")]
    [Consumes("multipart/form-data")]
    public async Task<Response> UploadAvatarMultipart(CancellationToken ct)
    {
        return await HandleAvatarUploadAsync(
            async (c) => await _accountRepository.GetPublicProfileAsync(PrincipalId, c),
            async (principal, stream, c) => await _avatarStorage.SaveSystemAvatarAsync(principal, stream, c),
            async (url, c) => CommandNoContent(await _uploadAvatarHandler.HandleAsync(BuildEnvelope(OperationIds.SettingsAvatarUpload, new UploadAvatarCommand(url, AvatarSource.Local)), c)),
            _avatarStorage,
            ct);
    }

    [HttpPut("avatar")]
    [Consumes("application/json")]
    public async Task<Response> UploadAvatarByUrl([FromBody] AvatarUrlUploadRequest req, CancellationToken ct)
    {
        if (req is null)
            return new ErrorResponse("Avatar URL payload required.", ErrorCodes.AvatarUrlInvalid, System.Net.HttpStatusCode.BadRequest);

        return await HandleAvatarUrlUploadAsync(
            req.Url.Value,
            async (c) => await _accountRepository.GetPublicProfileAsync(PrincipalId, c),
            async (url, c) => CommandNoContent(await _uploadAvatarHandler.HandleAsync(BuildEnvelope(OperationIds.SettingsAvatarUpload, new UploadAvatarCommand(url, AvatarSource.External)), c)),
            _avatarStorage,
            ct);
    }

    [HttpDelete("avatar")]
    public async Task<Response> DeleteAvatar(CancellationToken ct)
    {
        return await HandleAvatarDeleteAsync(
            async (c) => await _accountRepository.GetPublicProfileAsync(PrincipalId, c),
            async (c) => CommandNoContent(await _deleteAvatarHandler.HandleAsync(BuildEnvelope(OperationIds.SettingsAvatarDelete, new DeleteAvatarCommand()), c)),
            _avatarStorage,
            ct);
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
        var envelope = BuildEnvelope(OperationIds.SettingsImportPk, new ImportPkCommand(req.Token)
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

        var envelope = BuildEnvelope(OperationIds.SettingsImportSp, new ImportSpCommand(req.Token, recoveryCode)
        );

        return CommandAccepted(
            await _importSpHandler.HandleAsync(envelope, ct),
            r => new ImportDispatchResponse(r.OperationId, r.Status, r.StartedAt));
    }

    [HttpPost("unlink_discord")]
    public async Task<Response> UnlinkDiscord(CancellationToken ct)
    {
        return await DispatchNoContentAsync(_unlinkDiscordHandler, OperationIds.SettingsAuthUnlinkDiscord, new UnlinkDiscordCommand()
        , ct);
    }

    [HttpPost("unlink_email")]
    public async Task<Response> UnlinkEmail(CancellationToken ct)
    {
        return await DispatchNoContentAsync(_unlinkEmailHandler, OperationIds.SettingsAuthUnlinkEmail, new UnlinkEmailCommand()
        , ct);
    }

    [HttpPost("unlink_apple")]
    public async Task<Response> UnlinkApple(CancellationToken ct)
    {
        return await DispatchNoContentAsync(_unlinkAppleHandler, OperationIds.SettingsAuthUnlinkApple, new UnlinkAppleCommand()
        , ct);
    }

    [HttpPost("delete-account")]
    public async Task<Response> DeleteAccount(CancellationToken ct)
    {
        return await DispatchNoContentAsync(_deleteAccountHandler, OperationIds.SettingsAccountDelete, new DeleteAccountCommand()
        , ct);
    }

    [HttpPost("wipe-alters")]
    public async Task<Response> WipeAlters(CancellationToken ct)
    {
        return await DispatchNoContentAsync(_wipeAltersHandler, OperationIds.SettingsAltersWipe, new WipeAltersCommand()
        , ct);
    }

    [HttpPost("wipe-tags")]
    public async Task<Response> WipeTags(CancellationToken ct)
    {
        return await DispatchNoContentAsync(_wipeTagsHandler, OperationIds.SettingsTagsWipe, new WipeTagsCommand()
        , ct);
    }

    [HttpPost("fields")]
    public async Task<Response<FieldCreatedResponse>> CreateField([FromBody] SettingsCreateFieldRequest req, CancellationToken ct)
    {
        // CreateFieldCommandHandler owns InsertedAtUtc on the public-API path and derives it
        // from the envelope's OccurredAt right before calling the repo. Passing `default`
        // here keeps the hashed payload stable across retries with the same idempotency key
        // (otherwise every call would stamp a fresh DateTime.UtcNow and look like a
        // different request, triggering ConflictDuplicate on every replay).
        var envelope = BuildEnvelope(OperationIds.SettingsFieldCreate, new CreateFieldCommand(req.Name, req.Type ?? FieldType.Text, req.SecurityLevel ?? VisibilityLevel.Private, req.Locked ?? false, InsertedAtUtc: default)
        );

        var execution = await _createFieldHandler.HandleAsync(envelope, ct);
        if (!execution.Accepted)
            return ConflictToError(execution.Conflict!);

        return new SuccessResponse<FieldCreatedResponse>(new FieldCreatedResponse(execution.Result!.FieldId), System.Net.HttpStatusCode.Created, execution.Result.Replay);
    }

    [HttpPatch("fields/{id}")]
    public async Task<Response> UpdateField([FromRoute] FieldId id, [FromBody] SettingsUpdateFieldRequest req, CancellationToken ct)
    {
        return await DispatchNoContentAsync(_updateFieldHandler, OperationIds.SettingsFieldUpdate, new UpdateFieldCommand(id, req.Name, req.SecurityLevel, req.Locked)
        , ct);
    }

    [HttpDelete("fields/{id}")]
    public async Task<Response> DeleteField([FromRoute] FieldId id, CancellationToken ct)
    {
        return await DispatchNoContentAsync(_deleteFieldHandler, OperationIds.SettingsFieldDelete, new DeleteFieldCommand(id)
        , ct);
    }

    [HttpPost("fields/{id}/relocate")]
    public async Task<Response> RelocateField([FromRoute] FieldId id, [FromBody] SettingsRelocateFieldRequest req, CancellationToken ct)
    {
        return await DispatchNoContentAsync(_relocateFieldHandler, OperationIds.SettingsFieldRelocate, new RelocateFieldCommand(id, req.Index)
        , ct);
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

    /// <summary>
    /// Setup / recover both project to the same wire shape (base64-encoded UTF-8 bytes
    /// of the symmetric key material), and both handlers surface an
    /// <see cref="EncryptionCommandResult"/>, so the projection lives once here and
    /// both endpoints hand it to <c>DispatchOkAsync</c>.
    /// </summary>
    private static EncryptionKeyResponse ToEncryptionKeyResponse(EncryptionCommandResult result)
        => new(Convert.ToBase64String(Encoding.UTF8.GetBytes(result.Key.Value)));
}
