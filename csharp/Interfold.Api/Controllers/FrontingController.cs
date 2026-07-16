using Microsoft.AspNetCore.Mvc;
using Interfold.Api.ModelBinding;
using Interfold.Api.Models;
using Interfold.Contracts.Ids;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Models.Read;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Domain.Fronting;
using Interfold.Api.Controllers.Base;
using Interfold.Contracts;

namespace Interfold.Api.Controllers;

[Route("api/systems/me/front")]
public sealed class FrontingController : InterfoldControllerBase
{
    private readonly IFrontingRepository _repository;
    private readonly StartFrontCommandHandler _startHandler;
    private readonly EndFrontCommandHandler _endHandler;
    private readonly BulkUpdateFrontCommandHandler _bulkUpdateHandler;
    private readonly SetFrontCommandHandler _setHandler;
    private readonly SetPrimaryFrontCommandHandler _primaryHandler;
    private readonly DeleteFrontByIdCommandHandler _deleteByIdHandler;
    private readonly UpdateFrontCommentCommandHandler _updateCommentHandler;

    public FrontingController(
        IFrontingRepository repository,
        StartFrontCommandHandler startHandler,
        EndFrontCommandHandler endHandler,
        BulkUpdateFrontCommandHandler bulkUpdateHandler,
        SetFrontCommandHandler setHandler,
        SetPrimaryFrontCommandHandler primaryHandler,
        DeleteFrontByIdCommandHandler deleteByIdHandler,
        UpdateFrontCommentCommandHandler updateCommentHandler)
    {
        _repository = repository;
        _startHandler = startHandler;
        _endHandler = endHandler;
        _bulkUpdateHandler = bulkUpdateHandler;
        _setHandler = setHandler;
        _primaryHandler = primaryHandler;
        _deleteByIdHandler = deleteByIdHandler;
        _updateCommentHandler = updateCommentHandler;
    }

    //TODO: To ensure route works as expected
    [HttpPost]
    public async Task<Response> Update([FromBody] FrontBulkUpdateRequest req, CancellationToken ct)
    {
        var payload = new BulkUpdateFrontCommand(
            req.Start.Select(x => new FrontStartItem(x.AlterId, x.Comment)).ToArray(),
            req.End.ToArray());

        var envelope = new CommandEnvelope<BulkUpdateFrontCommand>(
            OperationIds.FrontBulkUpdate,
            Guid.NewGuid(),
            PrincipalId: PrincipalId,
            IdempotencyKey: GetIdempotencyKey(),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: payload
        );

        return CommandNoContent(await _bulkUpdateHandler.HandleAsync(envelope, ct));
    }

    [HttpPost("start")]
    public async Task<Response<FrontStartedResponse>> Start([FromBody] FrontStartRequest req, CancellationToken ct)
    {
        var envelope = new CommandEnvelope<StartFrontCommand>(
            OperationIds.FrontStart, Guid.NewGuid(),
            PrincipalId: PrincipalId,
            IdempotencyKey: GetIdempotencyKey(),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new StartFrontCommand(req.Id, req.Comment)
        );

        var execution = await _startHandler.HandleAsync(envelope, ct);
        var response = CommandCreated(execution, r => new FrontStartedResponse(r.FrontId!.Value), r => r?.Replay);

        if (response.IsSuccess && response.AsSuccess.Data.FrontId != FrontId.Empty)
        {
            Response.Headers.Location = $"/api/systems/me/front/{response.AsSuccess.Data.FrontId}";
        }

        return response;
    }

    [HttpPost("end")]
    public async Task<Response> End([FromBody] FrontEndRequest req, CancellationToken ct)
    {
        var envelope = new CommandEnvelope<EndFrontCommand>(
            OperationIds.FrontEnd, Guid.NewGuid(),
            PrincipalId: PrincipalId,
            IdempotencyKey: GetIdempotencyKey(),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new EndFrontCommand(req.Id)
        );

        return CommandNoContent(await _endHandler.HandleAsync(envelope, ct));
    }

    [HttpPost("set")]
    public async Task<Response> Set([FromBody] FrontSetRequest req, CancellationToken ct)
    {
        var envelope = new CommandEnvelope<SetFrontCommand>(
            OperationIds.FrontSet,
            Guid.NewGuid(),
            PrincipalId: PrincipalId,
            IdempotencyKey: GetIdempotencyKey(),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new SetFrontCommand(req.Id, req.Comment)
        );

        return CommandNoContent(await _setHandler.HandleAsync(envelope, ct));
    }

    [HttpPost("primary")]
    public async Task<Response> Primary([FromBody] FrontPrimaryRequest req, CancellationToken ct)
    {
        var envelope = new CommandEnvelope<SetPrimaryFrontCommand>(
            OperationIds.FrontPrimary, Guid.NewGuid(),
            PrincipalId: PrincipalId,
            IdempotencyKey: GetIdempotencyKey(),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new SetPrimaryFrontCommand(req.Id)
        );

        return CommandNoContent(await _primaryHandler.HandleAsync(envelope, ct));
    }

    //TODO: To ensure route works as expected
    [HttpGet("month")]
    public async Task<Response<IReadOnlyList<FrontHistoryReadModel>>> Month(
        [FromQuery(Name = FrontingQueryKeys.EndAnchor)]
        [UnixSecondsBinding(
            ErrorCode = "invalid_end_anchor",
            ErrorMessage = "Invalid end anchor. Please pass a valid Unix timestamp.")]
        UnixSeconds endAnchor,
        CancellationToken ct)
    {
        var end = endAnchor.ToDateTimeOffset();
        var start = end.AddDays(-30);
        var fronts = await _repository.ListHistoryBetweenAsync(PrincipalId, start, end, ct);
        return new SuccessResponse<IReadOnlyList<FrontHistoryReadModel>>(fronts);
    }

    [HttpGet("between")]
    public async Task<Response<IReadOnlyList<FrontHistoryReadModel>>> Between(
        [FromQuery(Name = FrontingQueryKeys.Start)]
        [UnixSecondsBinding(
            ErrorCode = "invalid_anchor",
            ErrorMessage = "Invalid start anchor. Please pass a valid Unix timestamp.")]
        UnixSeconds startAnchor,
        [FromQuery(Name = FrontingQueryKeys.End)]
        [UnixSecondsBinding(
            ErrorCode = "invalid_anchor",
            ErrorMessage = "Invalid end anchor. Please pass a valid Unix timestamp.")]
        UnixSeconds endAnchor,
        CancellationToken ct)
    {
        var fronts = await _repository.ListHistoryBetweenAsync(
            PrincipalId,
            startAnchor.ToDateTimeOffset(),
            endAnchor.ToDateTimeOffset(),
            ct);
        return new SuccessResponse<IReadOnlyList<FrontHistoryReadModel>>(fronts);
    }

    //TODO: To ensure route works as expected
    [HttpGet("{id}")]
    public async Task<Response<FrontActiveReadModel>> Show(FrontId id, CancellationToken ct)
    {
        var front = await _repository.GetActiveByFrontIdAsync(PrincipalId, id, ct);
        return front is null
            ? new ErrorResponse("Front not found.", ErrorCodes.FrontNotFound, System.Net.HttpStatusCode.NotFound)
            : new SuccessResponse<FrontActiveReadModel>(front);
    }

    [HttpDelete("{id}")]
    public async Task<Response> Delete(FrontId id, CancellationToken ct)
    {
        var envelope = new CommandEnvelope<DeleteFrontByIdCommand>(
            OperationIds.FrontDelete,
            Guid.NewGuid(),
            PrincipalId: PrincipalId,
            IdempotencyKey: GetIdempotencyKey(),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new DeleteFrontByIdCommand(id)
        );

        return CommandNoContent(await _deleteByIdHandler.HandleAsync(envelope, ct));
    }

    [HttpPost("{id}/comment")]
    public async Task<Response> UpdateComment(FrontId id, [FromBody] FrontCommentRequest req, CancellationToken ct)
    {
        var envelope = new CommandEnvelope<UpdateFrontCommentCommand>(
            OperationIds.FrontCommentUpdate,
            Guid.NewGuid(),
            PrincipalId: PrincipalId,
            IdempotencyKey: GetIdempotencyKey(),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new UpdateFrontCommentCommand(id, req.Comment)
        );

        return CommandNoContent(await _updateCommentHandler.HandleAsync(envelope, ct));
    }
}