using Interfold.Api.Models;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models.Commands;
using Interfold.Shared.Contracts.Models.Read;
using Microsoft.AspNetCore.Mvc;
using Interfold.Shared.Contracts.Operations;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Domain.Friendships;
using Interfold.Api.Controllers.Base;
using Interfold.Shared.Contracts;
using Interfold.Friendships.Api.Helpers;

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
            .Select(r => FriendshipAvatarQualifier.QualifyFriendRequest(r, Request.Scheme, Request.Host))
            .ToArray();
        var outgoing = requests.Outgoing
            .Select(r => FriendshipAvatarQualifier.QualifyFriendRequest(r, Request.Scheme, Request.Host))
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
        // Semantic self-check via the FriendLookup overload — catches the "client
        // sent their own id" fast-path case without a repository hop. The overload
        // fires for both Kind.Id (delegates to the SystemId primitive so raw and
        // same-region-scoped inputs both self-reject) and Kind.Username (trivially
        // returns false — deciding "is alice me?" requires a registry lookup, so
        // SendFriendRequestCommandHandler's post-resolution guard takes over).
        if (RejectIfSelf(id, "You cannot send a friend request to yourself.", ErrorCodes.CannotSendSelf) is { } reject)
            return reject;

        return await DispatchNoContentAsync(_send, OperationIds.FriendRequestSend, new SendFriendRequestCommand(id), ct);
    }

    [HttpDelete("{id}")]
    public async Task<Response> Cancel(SystemId id, CancellationToken ct)
    {
        // Semantic self-check — CancelFriendRequestCommandHandler has no downstream
        // self-guard, so a bare byte compare would let a raw-id self-cancel return the
        // generic friend_request:not_requested error instead of cannot_cancel_self.
        if (RejectIfSelf(id, "You cannot cancel a friend request to yourself.", ErrorCodes.CannotCancelSelf) is { } reject)
            return reject;

        return await DispatchNoContentAsync(_cancel, OperationIds.FriendRequestCancel, new CancelFriendRequestCommand(id), ct);
    }

    [HttpPost("{id}/accept")]
    public async Task<Response> Accept(SystemId id, CancellationToken ct)
    {
        if (RejectIfSelf(id, "You cannot accept a friend request from yourself.", ErrorCodes.CannotAcceptSelf) is { } reject)
            return reject;

        return await DispatchNoContentAsync(_accept, OperationIds.FriendRequestAccept, new AcceptFriendRequestCommand(id), ct);
    }

    [HttpPost("{id}/reject")]
    public async Task<Response> Reject(SystemId id, CancellationToken ct)
    {
        if (RejectIfSelf(id, "You cannot reject a friend request from yourself.", ErrorCodes.CannotRejectSelf) is { } reject)
            return reject;

        return await DispatchNoContentAsync(_reject, OperationIds.FriendRequestReject, new RejectFriendRequestCommand(id), ct);
    }
}
