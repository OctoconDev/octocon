using Microsoft.AspNetCore.Mvc;
using System.Text;
using Interfold.Api.Helpers;
using Interfold.Api.Models;
using Interfold.Api.Services;
using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Models.Read;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Domain.Alters;
using Interfold.Api.Controllers.Base;
using Interfold.Contracts;
using Interfold.Contracts.Validation;

namespace Interfold.Api.Controllers;

[Route("api/systems/me/alters")]
public sealed class AltersController : InterfoldControllerBase
{
    private readonly IAlterRepository _alterRepository;
    private readonly CreateAlterCommandHandler _createHandler;
    private readonly UpdateAlterCommandHandler _updateHandler;
    private readonly DeleteAlterCommandHandler _deleteHandler;
    private readonly IAvatarStorage _avatarStorage;

    public AltersController(
        IAlterRepository alterRepository,
        CreateAlterCommandHandler createHandler,
        UpdateAlterCommandHandler updateHandler,
        DeleteAlterCommandHandler deleteHandler,
        IAvatarStorage avatarStorage)
    {
        _alterRepository = alterRepository;
        _createHandler = createHandler;
        _updateHandler = updateHandler;
        _deleteHandler = deleteHandler;
        _avatarStorage = avatarStorage;
    }

    [HttpGet]
    public async Task<Response<IReadOnlyList<AlterReadModel>>> List(CancellationToken ct)
    {
        var alters = await _alterRepository.ListAsync(PrincipalId, ct);
        return new SuccessResponse<IReadOnlyList<AlterReadModel>>(alters);
    }

    [HttpGet("{alterId:int}")]
    public async Task<Response<AlterReadModel>> Show([FromRoute][ValidAlterId] AlterId alterId, CancellationToken ct)
    {
        var alter = await _alterRepository.GetAsync(PrincipalId, alterId, ct);
        if (alter is null)
        {
            return new ErrorResponse("Alter not found.", ErrorCodes.AlterNotFound, System.Net.HttpStatusCode.NotFound);
        }

        alter.AvatarUrl = QualifyAvatar(alter.AvatarUrl, alter.AvatarSource);
        return alter;
    }

    [HttpPost]
    public async Task<Response<AlterReadModel>> Create([FromBody] CreateAlterRequest req, CancellationToken ct)
    {
        var principal = PrincipalId;
        var envelope = new CommandEnvelope<CreateAlterCommand>(
            OperationIds.AlterCreate, Guid.NewGuid(),
            PrincipalId: principal,
            IdempotencyKey: GetIdempotencyKey(),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new CreateAlterCommand(req.Name, DateTimeOffset.UtcNow)
        );

        var execution = await _createHandler.HandleAsync(envelope, ct);
        if (!execution.Accepted)
        {
            return ConflictToError(execution.Conflict!);
        }

        var alter = await _alterRepository.GetAsync(principal, execution.Result!.AlterId, ct);
        if (alter is null)
            return new ErrorResponse("An unknown error occurred.", ErrorCodes.UnknownError, System.Net.HttpStatusCode.InternalServerError);

        Response.Headers.Location = $"/api/systems/me/alters/{execution.Result.AlterId}";
        return new SuccessResponse<AlterReadModel>(alter, System.Net.HttpStatusCode.Created, execution.Result.Replay);
    }

    [HttpPatch("{alterId:int}")]
    public async Task<Response> Update([FromRoute][ValidAlterId] AlterId alterId, [FromBody] UpdateAlterRequest req, CancellationToken ct)
    {
        var fields = req.Fields?.Select(f => new AlterFieldCommand(f.Id, f.Value)).ToList();

        // PATCH does not currently expose AvatarUrl as a mutable field — avatar updates go
        // through the dedicated PUT .../avatar endpoints (multipart for Local, JSON for External)
        // so we never observe a half-set (url-without-source) here.
        var payload = new UpdateAlterCommand(
            AlterId: alterId,
            Name: req.Name,
            Description: req.Description,
            AvatarUrl: null,
            AvatarSource: null,
            Color: req.Color,
            Pronouns: req.Pronouns,
            SecurityLevel: req.SecurityLevel,
            Fields: fields,
            ProxyName: req.ProxyName,
            Alias: req.Alias,
            Untracked: req.Untracked,
            Archived: req.Archived,
            Pinned: req.Pinned,
            UpdatedAt: DateTimeOffset.UtcNow
        );

        var envelope = new CommandEnvelope<UpdateAlterCommand>(
            OperationIds.AlterUpdate, Guid.NewGuid(),
            PrincipalId: PrincipalId,
            IdempotencyKey: GetIdempotencyKey(),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: payload
        );

        return CommandNoContent(await _updateHandler.HandleAsync(envelope, ct));
    }

    //TODO: To ensure route works as expected - check if we delete alter journal entries, unattach from gobal journals when an alter is deleted and delete them from polls
    [HttpDelete("{alterId:int}")]
    public async Task<Response> Delete([FromRoute][ValidAlterId] AlterId alterId, CancellationToken ct)
    {
        var envelope = new CommandEnvelope<DeleteAlterCommand>(
            OperationIds.AlterDelete, Guid.NewGuid(),
            PrincipalId: PrincipalId,
            IdempotencyKey: GetIdempotencyKey(),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new DeleteAlterCommand(alterId)
        );
        return CommandNoContent(await _deleteHandler.HandleAsync(envelope, ct));
    }

    [HttpPut("{alterId:int}/avatar")]
    [Consumes("multipart/form-data")]
    public async Task<Response> UploadAvatarMultipart([FromRoute][ValidAlterId] AlterId alterId, CancellationToken ct)
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
                avatarUrl = await _avatarStorage.SaveAlterAvatarAsync(principal, alterId, avatarStream, ct);
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
            var existingAlter = await _alterRepository.GetAsync(principal, alterId, ct);
            currentAvatarUrl = existingAlter?.AvatarUrl;
            currentAvatarSource = existingAlter?.AvatarSource;
        }
        catch
        {
            // Best effort to clean up old avatar; this isn't critical but ideal for costs/storage
        }

        var payload = new UpdateAlterCommand(
            AlterId: alterId,
            Name: null,
            Description: null,
            AvatarUrl: avatarUrl,
            AvatarSource: AvatarSource.Local,
            Color: null,
            Pronouns: null,
            SecurityLevel: null,
            Fields: null,
            ProxyName: null,
            Alias: null,
            Untracked: null,
            Archived: null,
            Pinned: null,
            UpdatedAt: DateTimeOffset.UtcNow
        );

        var envelope = new CommandEnvelope<UpdateAlterCommand>(
            OperationIds.AlterAvatarUpload,
            Guid.NewGuid(),
            PrincipalId: principal,
            IdempotencyKey: GetIdempotencyKey(),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: payload
        );

        var result = CommandNoContent(await _updateHandler.HandleAsync(envelope, ct));
        if (!result.IsSuccess) 
        {
            return result;
        }

        // Only the local storage owns the previous bytes; an external URL was never ours
        // to delete.
        if (currentAvatarSource == AvatarSource.Local)
        {
            try
            {
                await _avatarStorage.DeleteByUrlAsync(currentAvatarUrl, ct);
            }
            catch
            {
                // Alter metadata update succeeded; tolerate storage cleanup failures.
            }
        }

        return result;
    }

    /// <summary>
    /// JSON sibling of <see cref="UploadAvatarMultipart"/>: stores the supplied URL on
    /// <c>avatar_url</c> with <c>avatar_source = External</c> without fetching the bytes.
    /// </summary>
    [HttpPut("{alterId:int}/avatar")]
    [Consumes("application/json")]
    public async Task<Response> UploadAvatarByUrl([FromRoute][ValidAlterId] AlterId alterId, [FromBody] AvatarUrlUploadRequest req, CancellationToken ct)
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
            var existingAlter = await _alterRepository.GetAsync(principal, alterId, ct);
            currentAvatarUrl = existingAlter?.AvatarUrl;
            currentAvatarSource = existingAlter?.AvatarSource;
        }
        catch
        {
        }

        var payload = new UpdateAlterCommand(
            AlterId: alterId,
            Name: null,
            Description: null,
            AvatarUrl: new(url),
            AvatarSource: AvatarSource.External,
            Color: null,
            Pronouns: null,
            SecurityLevel: null,
            Fields: null,
            ProxyName: null,
            Alias: null,
            Untracked: null,
            Archived: null,
            Pinned: null,
            UpdatedAt: DateTimeOffset.UtcNow
        );

        var envelope = new CommandEnvelope<UpdateAlterCommand>(
            OperationIds.AlterAvatarUpload,
            Guid.NewGuid(),
            PrincipalId: principal,
            IdempotencyKey: GetIdempotencyKey(),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: payload
        );

        var result = CommandNoContent(await _updateHandler.HandleAsync(envelope, ct));
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

    [HttpDelete("{alterId:int}/avatar")]
    public async Task<Response> DeleteAvatar([FromRoute][ValidAlterId] AlterId alterId, CancellationToken ct)
    {
        var principal = PrincipalId;

        var existingAlter = await _alterRepository.GetAsync(principal, alterId, ct);
        var currentAvatarUrl = existingAlter?.AvatarUrl;
        var currentAvatarSource = existingAlter?.AvatarSource;

        var payload = new UpdateAlterCommand(
            AlterId: alterId,
            Name: null,
            Description: null,
            AvatarUrl: null,
            AvatarSource: null,
            Color: null,
            Pronouns: null,
            SecurityLevel: null,
            Fields: null,
            ProxyName: null,
            Alias: null,
            Untracked: null,
            Archived: null,
            Pinned: null,
            UpdatedAt: DateTimeOffset.UtcNow,
            ClearAvatar: true
        );

        var envelope = new CommandEnvelope<UpdateAlterCommand>(
            OperationIds.AlterAvatarDelete,
            Guid.NewGuid(),
            PrincipalId: principal,
            IdempotencyKey: GetIdempotencyKey(),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: payload
        );

        var execution = await _updateHandler.HandleAsync(envelope, ct);
        if (execution.Accepted && currentAvatarSource == AvatarSource.Local)
        {
            try
            {
                 await _avatarStorage.DeleteByUrlAsync(currentAvatarUrl, ct);
            }
            catch
            {
                // Alter metadata update succeeded; tolerate storage cleanup failures.
            }
        }

        return CommandNoContent(execution);
    }
}