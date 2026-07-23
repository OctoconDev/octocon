using Microsoft.AspNetCore.Mvc;
using Interfold.Api.Models;
using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Operations;
using Interfold.Shared.Domain.Polls;
using Interfold.Shared.Contracts.Models.Commands;
using Interfold.Shared.Contracts.Models.Read;
using Interfold.Shared.Domain.Abstractions.Repository;
using Interfold.Api.Controllers.Base;

namespace Interfold.Api.Controllers;

[Route("api/polls")]
public sealed class PollsController : InterfoldControllerBase
{
    private readonly IPollRepository _pollRepository;
    private readonly CreatePollCommandHandler _create;
    private readonly UpdatePollCommandHandler _update;
    private readonly DeletePollCommandHandler _delete;

    public PollsController(
        IPollRepository pollRepository,
        CreatePollCommandHandler create,
        UpdatePollCommandHandler update,
        DeletePollCommandHandler delete)
    {
        _pollRepository = pollRepository;
        _create = create;
        _update = update;
        _delete = delete;
    }

    [HttpGet]
    public async Task<Response<IReadOnlyList<PollReadModel>>> Index(CancellationToken ct)
    {
        var polls = await _pollRepository.ListAsync(PrincipalId, ct);
        return new SuccessResponse<IReadOnlyList<PollReadModel>>(polls);
    }

    [HttpGet("{id}")]
    public async Task<Response<PollReadModel>> Show(PollId id, CancellationToken ct)
    {
        var poll = await _pollRepository.GetAsync(PrincipalId, id, ct);
        return OkOrNotFound(poll, "Poll not found.", ErrorCodes.PollNotFound);
    }

    [HttpPost]
    public async Task<Response<PollReadModel>> Create([FromBody] CreatePollRequest req, CancellationToken ct)
    {
        var principal = PrincipalId;
        // CreatePollCommandHandler owns InsertedAtUtc on the public-API path and derives it
        // from the envelope's OccurredAt right before calling the repo. Passing `default`
        // here keeps the hashed payload stable across retries with the same idempotency key
        // (otherwise every call would stamp a fresh DateTime.UtcNow and look like a
        // different request, triggering ConflictDuplicate on every replay).
        return await DispatchCreatedAsync(
            _create,
            OperationIds.PollCreate,
            new CreatePollCommand(req.Title, req.Description, req.Type ?? PollType.Vote, req.TimeEnd, InsertedAtUtc: default),
            async (res) => await _pollRepository.GetAsync(principal, res.PollId, ct),
            ct);
    }

    [HttpPatch("{id}")]
    public async Task<Response> Update(PollId id, [FromBody] UpdatePollRequest req, CancellationToken ct)
    {
        // time_end is tri-state (absent / null / value); an unparseable value surfaces as
        // PatchValueState.Invalid so we can keep this exact error body instead of MVC's
        // generic model-binding 400.
        if (req.TimeEnd.IsInvalid)
        {
            return new ErrorResponse("Invalid time_end.", ErrorCodes.PollInvalidTimeEnd, System.Net.HttpStatusCode.BadRequest);
        }

        DateTime? resolvedTimeEnd = req.TimeEnd.State == PatchValueState.Value ? req.TimeEnd.Value : null;

        return await DispatchNoContentAsync(_update, OperationIds.PollUpdate, new UpdatePollCommand(id, req.Title, req.Description, resolvedTimeEnd, req.TimeEnd.IsSet, req.Data)
        , ct);
    }

    [HttpDelete("{id}")]
    public async Task<Response> Delete(PollId id, CancellationToken ct)
    {
        return await DispatchNoContentAsync(_delete, OperationIds.PollDelete, new DeletePollCommand(id)
        , ct);
    }
}
