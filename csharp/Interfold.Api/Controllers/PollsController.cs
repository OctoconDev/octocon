using Microsoft.AspNetCore.Mvc;
using Interfold.Api.Models;
using Interfold.Contracts;
using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;
using Interfold.Contracts.Operations;
using Interfold.Domain.Polls;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Models.Read;
using Interfold.Domain.Abstractions.Repository;
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

    //TODO: To ensure route works as expected
    [HttpGet("{id}")]
    public async Task<Response<PollReadModel>> Show(PollId id, CancellationToken ct)
    {
        var poll = await _pollRepository.GetAsync(PrincipalId, id, ct);
        return poll is null
            ? new ErrorResponse("Poll not found.", ErrorCodes.PollNotFound, System.Net.HttpStatusCode.NotFound)
            : poll;
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
        var envelope = new CommandEnvelope<CreatePollCommand>(
            OperationIds.PollCreate,
            Guid.NewGuid(),
            PrincipalId: principal,
            IdempotencyKey: GetIdempotencyKey(),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new CreatePollCommand(req.Title, req.Description, req.Type ?? PollType.Vote, req.TimeEnd, InsertedAtUtc: default)
        );

        var execution = await _create.HandleAsync(envelope, ct);
        if (!execution.Accepted)
        {
            return ConflictToError(execution.Conflict!);
        }

        var poll = await _pollRepository.GetAsync(principal, execution.Result!.PollId, ct);
        if (poll is null)
            return new ErrorResponse("An unknown error occurred.", ErrorCodes.UnknownError, System.Net.HttpStatusCode.InternalServerError);

        return new SuccessResponse<PollReadModel>(poll, System.Net.HttpStatusCode.Created, execution.Result.Replay);
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

        var envelope = new CommandEnvelope<UpdatePollCommand>(
            OperationIds.PollUpdate,
            Guid.NewGuid(),
            PrincipalId: PrincipalId,
            IdempotencyKey: GetIdempotencyKey(),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new UpdatePollCommand(id, req.Title, req.Description, resolvedTimeEnd, req.TimeEnd.IsSet, req.Data)
        );

        return CommandNoContent(await _update.HandleAsync(envelope, ct));
    }

    [HttpDelete("{id}")]
    public async Task<Response> Delete(PollId id, CancellationToken ct)
    {
        var envelope = new CommandEnvelope<DeletePollCommand>(
            OperationIds.PollDelete,
            Guid.NewGuid(),
            PrincipalId: PrincipalId,
            IdempotencyKey: GetIdempotencyKey(),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new DeletePollCommand(id)
        );

        return CommandNoContent(await _delete.HandleAsync(envelope, ct));
    }
}
