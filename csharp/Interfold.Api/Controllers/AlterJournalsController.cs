using System.Net;
using Interfold.Api.Models;
using Interfold.Contracts.Ids;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Models.Read;
using Microsoft.AspNetCore.Mvc;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Domain.Journals;
using Interfold.Api.Controllers.Base;
using Interfold.Contracts;
using Interfold.Contracts.Validation;

namespace Interfold.Api.Controllers;

[Route("api/systems/me/alters")]
public sealed class AlterJournalsController : InterfoldControllerBase
{
    private readonly IJournalRepository _journalRepository;
    private readonly CreateAlterJournalEntryCommandHandler _create;
    private readonly UpdateAlterJournalEntryCommandHandler _update;
    private readonly DeleteAlterJournalEntryCommandHandler _delete;
    private readonly SetAlterJournalLockedCommandHandler _setLocked;
    private readonly SetAlterJournalPinnedCommandHandler _setPinned;

    public AlterJournalsController(
        IJournalRepository journalRepository,
        CreateAlterJournalEntryCommandHandler create,
        UpdateAlterJournalEntryCommandHandler update,
        DeleteAlterJournalEntryCommandHandler delete,
        SetAlterJournalLockedCommandHandler setLocked,
        SetAlterJournalPinnedCommandHandler setPinned)
    {
        _journalRepository = journalRepository;
        _create = create;
        _update = update;
        _delete = delete;
        _setLocked = setLocked;
        _setPinned = setPinned;
    }

    [HttpGet("{alterId:int}/journals")]
    public async Task<Response<IEnumerable<AlterJournalReadModel>>> Index([FromRoute][ValidAlterId] AlterId alterId, CancellationToken ct)
    {
        var principal = PrincipalId;
        IEnumerable<AlterJournalReadModel> entries = await _journalRepository.ListAlterAsync(principal, alterId, ct);
        return new SuccessResponse<IEnumerable<AlterJournalReadModel>>(entries);
    }

    [HttpGet("journals/{journalId}")]
    public async Task<Response<AlterJournalReadModel>> Show(EntryId journalId, CancellationToken ct)
    {
        var entry = await _journalRepository.GetAlterAsync(PrincipalId, journalId, ct);
        return entry is null 
            ? new ErrorResponse("Journal entry not found", ErrorCodes.JournalEntryNotFound, HttpStatusCode.NotFound) 
            : new SuccessResponse<AlterJournalReadModel>(entry);
    }

    [HttpPost("{alterId:int}/journals")]
    public async Task<Response<AlterJournalReadModel>> Create([FromRoute][ValidAlterId] AlterId alterId, [FromBody] CreateAlterJournalRequest req, CancellationToken ct)
    {
        var principal = PrincipalId;

        var envelope = new CommandEnvelope<CreateAlterJournalEntryCommand>(
            OperationIds.JournalAlterCreate,
            Guid.NewGuid(),
            PrincipalId: principal,
            IdempotencyKey: GetIdempotencyKey(),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new CreateAlterJournalEntryCommand(alterId, req.Title, DateTimeOffset.UtcNow)
        );

        var execution = await _create.HandleAsync(envelope, ct);
        if (!execution.Accepted)
        {
            return ConflictToError(execution.Conflict!);
        }

        var entry = await _journalRepository.GetAlterAsync(principal, execution.Result!.EntryId, ct);
        if (entry is null)
            return new ErrorResponse("An unknown error occurred.", ErrorCodes.UnknownError, HttpStatusCode.InternalServerError);

        return new SuccessResponse<AlterJournalReadModel>(entry, HttpStatusCode.Created, execution.Result.Replay);
    }

    [HttpPatch("journals/{journalId}")]
    public async Task<Response> Update(EntryId journalId, [FromBody] UpdateAlterJournalRequest req, CancellationToken ct)
    {
        var envelope = new CommandEnvelope<UpdateAlterJournalEntryCommand>(
            OperationIds.JournalAlterUpdate,
            Guid.NewGuid(),
            PrincipalId: PrincipalId,
            IdempotencyKey: GetIdempotencyKey(),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new UpdateAlterJournalEntryCommand(journalId, req.Title, req.Content, req.Color, DateTimeOffset.UtcNow)
        );

        return CommandNoContent(await _update.HandleAsync(envelope, ct));
    }

    [HttpDelete("journals/{journalId}")]
    public async Task<Response> Delete(EntryId journalId, CancellationToken ct)
    {
        var envelope = new CommandEnvelope<DeleteAlterJournalEntryCommand>(
            OperationIds.JournalAlterDelete,
            Guid.NewGuid(),
            PrincipalId: PrincipalId,
            IdempotencyKey: GetIdempotencyKey(),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new DeleteAlterJournalEntryCommand(journalId)
        );

        return CommandNoContent(await _delete.HandleAsync(envelope, ct));
    }

    [HttpPost("journals/{journalId}/lock")]
    public async Task<Response> Lock(EntryId journalId, CancellationToken ct)
        => await SetLockedInternal(journalId, true, OperationIds.JournalAlterLock, ct);

    [HttpPost("journals/{journalId}/unlock")]
    public async Task<Response> Unlock(EntryId journalId, CancellationToken ct)
        => await SetLockedInternal(journalId, false, OperationIds.JournalAlterUnlock, ct);

    [HttpPost("journals/{journalId}/pin")]
    public async Task<Response> Pin(EntryId journalId, CancellationToken ct)
        => await SetPinnedInternal(journalId, true, OperationIds.JournalAlterPin, ct);

    [HttpPost("journals/{journalId}/unpin")]
    public async Task<Response> Unpin(EntryId journalId, CancellationToken ct)
        => await SetPinnedInternal(journalId, false, OperationIds.JournalAlterUnpin, ct);

    private async Task<Response> SetLockedInternal(EntryId journalId, bool locked, OperationId operationId, CancellationToken ct)
    {
        var envelope = new CommandEnvelope<SetAlterJournalLockedCommand>(
            operationId,
            Guid.NewGuid(),
            PrincipalId: PrincipalId,
            IdempotencyKey: GetIdempotencyKey(),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new SetAlterJournalLockedCommand(journalId, locked)
        );

        return CommandNoContent(await _setLocked.HandleAsync(envelope, ct));
    }

    private async Task<Response> SetPinnedInternal(EntryId journalId, bool pinned, OperationId operationId, CancellationToken ct)
    {
        var envelope = new CommandEnvelope<SetAlterJournalPinnedCommand>(
            operationId,
            Guid.NewGuid(),
            PrincipalId: PrincipalId,
            IdempotencyKey: GetIdempotencyKey(),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new SetAlterJournalPinnedCommand(journalId, pinned)
        );

        return CommandNoContent(await _setPinned.HandleAsync(envelope, ct));
    }
}