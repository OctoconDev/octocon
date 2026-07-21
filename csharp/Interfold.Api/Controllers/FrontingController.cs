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

        return await DispatchNoContentAsync(_bulkUpdateHandler, OperationIds.FrontBulkUpdate, payload, ct);
    }

    [HttpPost("start")]
    public async Task<Response<FrontStartedResponse>> Start([FromBody] FrontStartRequest req, CancellationToken ct)
    {
        var envelope = BuildEnvelope(OperationIds.FrontStart, new StartFrontCommand(req.Id, req.Comment)
        );

        var execution = await _startHandler.HandleAsync(envelope, ct);
        var response = CommandCreated(execution, r => new FrontStartedResponse(r.FrontId!.Value));

        if (response.IsSuccess && response.AsSuccess.Data.FrontId != FrontId.Empty)
        {
            Response.Headers.Location = $"/api/systems/me/front/{response.AsSuccess.Data.FrontId}";
        }

        return response;
    }

    [HttpPost("end")]
    public async Task<Response> End([FromBody] FrontEndRequest req, CancellationToken ct)
    {
        return await DispatchNoContentAsync(_endHandler, OperationIds.FrontEnd, new EndFrontCommand(req.Id)
        , ct);
    }

    [HttpPost("set")]
    public async Task<Response> Set([FromBody] FrontSetRequest req, CancellationToken ct)
    {
        return await DispatchNoContentAsync(_setHandler, OperationIds.FrontSet, new SetFrontCommand(req.Id, req.Comment)
        , ct);
    }

    [HttpPost("primary")]
    public async Task<Response> Primary([FromBody] FrontPrimaryRequest req, CancellationToken ct)
    {
        return await DispatchNoContentAsync(_primaryHandler, OperationIds.FrontPrimary, new SetPrimaryFrontCommand(req.Id)
        , ct);
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
        return OkOrNotFound(front, "Front not found.", ErrorCodes.FrontNotFound);
    }

    [HttpDelete("{id}")]
    public async Task<Response> Delete(FrontId id, CancellationToken ct)
    {
        return await DispatchNoContentAsync(_deleteByIdHandler, OperationIds.FrontDelete, new DeleteFrontByIdCommand(id)
        , ct);
    }

    [HttpPost("{id}/comment")]
    public async Task<Response> UpdateComment(FrontId id, [FromBody] FrontCommentRequest req, CancellationToken ct)
    {
        return await DispatchNoContentAsync(_updateCommentHandler, OperationIds.FrontCommentUpdate, new UpdateFrontCommentCommand(id, req.Comment)
        , ct);
    }
}