using Interfold.Api.Models;
using Interfold.Contracts.Ids;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Models.Read;
using Microsoft.AspNetCore.Mvc;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Domain.Friendships;
using Interfold.Api.Controllers.Base;
using Interfold.Contracts;

namespace Interfold.Api.Controllers;

[Route("api/friend-requests")]
public sealed class FriendRequestsController : InterfoldControllerBase
{
    private readonly IFriendshipRepository _repository;
    private readonly SendFriendRequestCommandHandler _send;
    private readonly AcceptFriendRequestCommandHandler _accept;
    private readonly RejectFriendRequestCommandHandler _reject;
    private readonly CancelFriendRequestCommandHandler _cancel;

    public FriendRequestsController(
        IFriendshipRepository repository,
        SendFriendRequestCommandHandler send,
        AcceptFriendRequestCommandHandler accept,
        RejectFriendRequestCommandHandler reject,
        CancelFriendRequestCommandHandler cancel)
    {
        _repository = repository;
        _send = send;
        _accept = accept;
        _reject = reject;
        _cancel = cancel;
    }

    [HttpGet]
    public async Task<Response<FriendRequestIndexReadModel>> Index(CancellationToken ct)
    {
        var requests = await _repository.GetFriendRequestsAsync(PrincipalId, ct);
        var incoming = requests.Incoming
            .Select(x => x with
            {
                System = x.System with { AvatarUrl = QualifyAvatar(x.System.AvatarUrl, x.System.AvatarSource) }
            })
            .ToArray();
        var outgoing = requests.Outgoing
            .Select(x => x with
            {
                System = x.System with { AvatarUrl = QualifyAvatar(x.System.AvatarUrl, x.System.AvatarSource) }
            })
            .ToArray();

        // Same wire shape as the previous anonymous object: {"data":{"incoming":[…],"outgoing":[…]}}.
        return new SuccessResponse<FriendRequestIndexReadModel>(new FriendRequestIndexReadModel(incoming, outgoing));
    }

    // The route value is bound as FriendLookup, which accepts exactly two shapes:
    // a bare (or "id:"-prefixed) system id, and a "username:"-prefixed username. Every
    // other shape — Discord snowflakes, region-scoped ids (nam:...), unknown prefixes,
    // blank / half inputs — fails FriendLookup.TryParse and ASP.NET Core's IParsable
    // pipeline surfaces a 400 before this action runs. The command handler resolves
    // Kind.Username via IFriendshipRepository.ResolveUserIdAsync.
    [HttpPut("{id}")]
    public async Task<Response> Send(FriendLookup id, CancellationToken ct)
    {
        var principal = PrincipalId;
        // Semantic self-check via the FriendLookup overload — catches the "client
        // sent their own id" fast-path case without a repository hop. The overload
        // fires for both Kind.Id (delegates to the SystemId primitive so raw and
        // same-region-scoped inputs both self-reject) and Kind.Username (trivially
        // returns false — deciding "is alice me?" requires a registry lookup, so
        // SendFriendRequestCommandHandler's post-resolution guard takes over).
        if (principal.RepresentsSameUserAs(id))
        {
            return new ErrorResponse(
                "You cannot send a friend request to yourself.",
                ErrorCodes.CannotSendSelf,
                System.Net.HttpStatusCode.BadRequest);
        }

        var envelope = new CommandEnvelope<SendFriendRequestCommand>(
            OperationIds.FriendRequestSend,
            Guid.NewGuid(),
            PrincipalId: principal,
            IdempotencyKey: GetIdempotencyKey(),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new SendFriendRequestCommand(id));

        return CommandNoContent(await _send.HandleAsync(envelope, ct));
    }

    [HttpDelete("{id}")]
    public async Task<Response> Cancel(SystemId id, CancellationToken ct)
    {
        var principal = PrincipalId;
        // Semantic self-check — CancelFriendRequestCommandHandler has no downstream
        // self-guard, so a bare byte compare would let a raw-id self-cancel return the
        // generic friend_request:not_requested error instead of cannot_cancel_self.
        if (principal.RepresentsSameUserAs(id))
        {
            return new ErrorResponse(
                "You cannot cancel a friend request to yourself.",
                ErrorCodes.CannotCancelSelf,
                System.Net.HttpStatusCode.BadRequest);
        }

        var envelope = new CommandEnvelope<CancelFriendRequestCommand>(
            OperationIds.FriendRequestCancel,
            Guid.NewGuid(),
            PrincipalId: principal,
            IdempotencyKey: GetIdempotencyKey(),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new CancelFriendRequestCommand(id));

        return CommandNoContent(await _cancel.HandleAsync(envelope, ct));
    }

    [HttpPost("{id}/accept")]
    public async Task<Response> Accept(SystemId id, CancellationToken ct)
    {
        var principal = PrincipalId;
        // Semantic self-check — see Cancel handler for the same rationale.
        if (principal.RepresentsSameUserAs(id))
        {
            return new ErrorResponse(
                "You cannot accept a friend request from yourself.",
                ErrorCodes.CannotAcceptSelf,
                System.Net.HttpStatusCode.BadRequest);
        }

        var envelope = new CommandEnvelope<AcceptFriendRequestCommand>(
            OperationIds.FriendRequestAccept,
            Guid.NewGuid(),
            PrincipalId: principal,
            IdempotencyKey: GetIdempotencyKey(),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new AcceptFriendRequestCommand(id));

        return CommandNoContent(await _accept.HandleAsync(envelope, ct));
    }

    [HttpPost("{id}/reject")]
    public async Task<Response> Reject(SystemId id, CancellationToken ct)
    {
        var principal = PrincipalId;
        // Semantic self-check — see Cancel handler for the same rationale.
        if (principal.RepresentsSameUserAs(id))
        {
            return new ErrorResponse(
                "You cannot reject a friend request from yourself.",
                ErrorCodes.CannotRejectSelf,
                System.Net.HttpStatusCode.BadRequest);
        }

        var envelope = new CommandEnvelope<RejectFriendRequestCommand>(
            OperationIds.FriendRequestReject,
            Guid.NewGuid(),
            PrincipalId: principal,
            IdempotencyKey: GetIdempotencyKey(),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new RejectFriendRequestCommand(id));

        return CommandNoContent(await _reject.HandleAsync(envelope, ct));
    }
}
