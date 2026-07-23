using Interfold.Api.Models;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models.Commands;
using Interfold.Shared.Contracts.Models.Read;
using Microsoft.AspNetCore.Mvc;
using Interfold.Shared.Contracts.Operations;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Domain.Journals;
using Interfold.Api.Controllers.Base;
using Interfold.Shared.Contracts;

namespace Interfold.Api.Controllers;

[Route("api/journals")]
public sealed class JournalsController : InterfoldControllerBase
{
    private readonly IJournalRepository _journalRepository;
    private readonly CreateGlobalJournalEntryCommandHandler _create;
    private readonly UpdateGlobalJournalEntryCommandHandler _update;
    private readonly DeleteGlobalJournalEntryCommandHandler _delete;
    private readonly SetGlobalJournalLockedCommandHandler _setLocked;
    private readonly SetGlobalJournalPinnedCommandHandler _setPinned;
    private readonly AttachAlterToGlobalJournalCommandHandler _attachAlter;
    private readonly DetachAlterFromGlobalJournalCommandHandler _detachAlter;

    public JournalsController(
        IJournalRepository journalRepository,
        CreateGlobalJournalEntryCommandHandler create,
        UpdateGlobalJournalEntryCommandHandler update,
        DeleteGlobalJournalEntryCommandHandler delete,
        SetGlobalJournalLockedCommandHandler setLocked,
        SetGlobalJournalPinnedCommandHandler setPinned,
        AttachAlterToGlobalJournalCommandHandler attachAlter,
        DetachAlterFromGlobalJournalCommandHandler detachAlter)
    {
        _journalRepository = journalRepository;
        _create = create;
        _update = update;
        _delete = delete;
        _setLocked = setLocked;
        _setPinned = setPinned;
        _attachAlter = attachAlter;
        _detachAlter = detachAlter;
    }

    [HttpGet]
    public async Task<Response<IReadOnlyList<JournalReadModel>>> Index(CancellationToken ct)
    {
        var entries = await _journalRepository.ListGlobalAsync(PrincipalId, ct);
        return new SuccessResponse<IReadOnlyList<JournalReadModel>>(entries);
    }

    [HttpGet("{id}")]
    public async Task<Response<JournalReadModel>> Show(EntryId id, CancellationToken ct)
    {
        var entry = await _journalRepository.GetGlobalAsync(PrincipalId, id, ct);
        return OkOrNotFound(entry, "Journal entry not found.", ErrorCodes.JournalEntryNotFound);
    }

    [HttpPost]
    public async Task<Response<JournalReadModel>> Create([FromBody] CreateGlobalJournalRequest req, CancellationToken ct)
    {
        var principal = PrincipalId;
        return await DispatchCreatedAsync(
            _create,
            OperationIds.JournalGlobalCreate,
            new CreateGlobalJournalEntryCommand(req.Title),
            async (res) => await _journalRepository.GetGlobalAsync(principal, res.EntryId, ct),
            ct);
    }

    [HttpPatch("{id}")]
    public async Task<Response> Update(EntryId id, [FromBody] UpdateGlobalJournalRequest req, CancellationToken ct)
    {
        return await DispatchNoContentAsync(_update, OperationIds.JournalGlobalUpdate, new UpdateGlobalJournalEntryCommand(id, req.Title, req.Content, req.Color)
        , ct);
    }

    [HttpDelete("{id}")]
    public async Task<Response> Delete(EntryId id, [FromBody] DeleteGlobalJournalRequest? req, CancellationToken ct)
    {
        return await DispatchNoContentAsync(_delete, OperationIds.JournalGlobalDelete, new DeleteGlobalJournalEntryCommand(id)
        , ct);
    }

    [HttpPost("{id}/lock")]
    public Task<Response> Lock(EntryId id, [FromBody] JournalActionRequest? req, CancellationToken ct)
        => DispatchNoContentAsync(_setLocked, OperationIds.JournalGlobalLock, new SetGlobalJournalLockedCommand(id, true), ct);

    [HttpPost("{id}/unlock")]
    public Task<Response> Unlock(EntryId id, [FromBody] JournalActionRequest? req, CancellationToken ct)
        => DispatchNoContentAsync(_setLocked, OperationIds.JournalGlobalUnlock, new SetGlobalJournalLockedCommand(id, false), ct);

    [HttpPost("{id}/pin")]
    public Task<Response> Pin(EntryId id, [FromBody] JournalActionRequest? req, CancellationToken ct)
        => DispatchNoContentAsync(_setPinned, OperationIds.JournalGlobalPin, new SetGlobalJournalPinnedCommand(id, true), ct);

    [HttpPost("{id}/unpin")]
    public Task<Response> Unpin(EntryId id, [FromBody] JournalActionRequest? req, CancellationToken ct)
        => DispatchNoContentAsync(_setPinned, OperationIds.JournalGlobalUnpin, new SetGlobalJournalPinnedCommand(id, false), ct);

    [HttpPost("{id}/alter")]
    public async Task<Response> AttachAlter(EntryId id, [FromBody] JournalAlterRequest req, CancellationToken ct)
    {
        return await DispatchNoContentAsync(_attachAlter, OperationIds.JournalGlobalAttachAlter, new AttachAlterToGlobalJournalCommand(id, req.AlterId)
        , ct);
    }

    [HttpDelete("{id}/alter")]
    public async Task<Response> DetachAlter(EntryId id, [FromBody] JournalAlterRequest req, CancellationToken ct)
    {
        return await DispatchNoContentAsync(_detachAlter, OperationIds.JournalGlobalDetachAlter, new DetachAlterFromGlobalJournalCommand(id, req.AlterId)
        , ct);
    }
}