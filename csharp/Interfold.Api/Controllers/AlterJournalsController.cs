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
        return OkOrNotFound(entry, "Journal entry not found.", ErrorCodes.JournalEntryNotFound);
    }

    [HttpPost("{alterId:int}/journals")]
    public async Task<Response<AlterJournalReadModel>> Create([FromRoute][ValidAlterId] AlterId alterId, [FromBody] CreateAlterJournalRequest req, CancellationToken ct)
    {
        var principal = PrincipalId;
        return await DispatchCreatedAsync(
            _create,
            OperationIds.JournalAlterCreate,
            new CreateAlterJournalEntryCommand(alterId, req.Title, TimeProvider.GetUtcNow()),
            async (res) => await _journalRepository.GetAlterAsync(principal, res.EntryId, ct),
            ct);
    }

    [HttpPatch("journals/{journalId}")]
    public async Task<Response> Update(EntryId journalId, [FromBody] UpdateAlterJournalRequest req, CancellationToken ct)
    {
        return await DispatchNoContentAsync(_update, OperationIds.JournalAlterUpdate, new UpdateAlterJournalEntryCommand(journalId, req.Title, req.Content, req.Color, TimeProvider.GetUtcNow())
        , ct);
    }

    [HttpDelete("journals/{journalId}")]
    public async Task<Response> Delete(EntryId journalId, CancellationToken ct)
    {
        return await DispatchNoContentAsync(_delete, OperationIds.JournalAlterDelete, new DeleteAlterJournalEntryCommand(journalId)
        , ct);
    }

    [HttpPost("journals/{journalId}/lock")]
    public Task<Response> Lock(EntryId journalId, CancellationToken ct)
        => DispatchNoContentAsync(_setLocked, OperationIds.JournalAlterLock, new SetAlterJournalLockedCommand(journalId, true), ct);

    [HttpPost("journals/{journalId}/unlock")]
    public Task<Response> Unlock(EntryId journalId, CancellationToken ct)
        => DispatchNoContentAsync(_setLocked, OperationIds.JournalAlterUnlock, new SetAlterJournalLockedCommand(journalId, false), ct);

    [HttpPost("journals/{journalId}/pin")]
    public Task<Response> Pin(EntryId journalId, CancellationToken ct)
        => DispatchNoContentAsync(_setPinned, OperationIds.JournalAlterPin, new SetAlterJournalPinnedCommand(journalId, true), ct);

    [HttpPost("journals/{journalId}/unpin")]
    public Task<Response> Unpin(EntryId journalId, CancellationToken ct)
        => DispatchNoContentAsync(_setPinned, OperationIds.JournalAlterUnpin, new SetAlterJournalPinnedCommand(journalId, false), ct);
}