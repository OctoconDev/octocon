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

[Route("api/friends")]
public sealed class FriendsController : InterfoldControllerBase
{
    private readonly IFriendshipRepository _repository;
    private readonly RemoveFriendshipCommandHandler _remove;
    private readonly SetFriendTrustCommandHandler _setTrust;

    public FriendsController(
        IFriendshipRepository repository,
        RemoveFriendshipCommandHandler remove,
        SetFriendTrustCommandHandler setTrust)
    {
        _repository = repository;
        _remove = remove;
        _setTrust = setTrust;
    }

    [HttpGet]
    public async Task<Response<IReadOnlyList<FriendshipReadModel>>> Index(CancellationToken ct)
    {
        var friendships = await _repository.ListFriendshipsAsync(PrincipalId, ct);
        var qualified = friendships.Select(QualifyFriendship).ToArray();
        return new SuccessResponse<IReadOnlyList<FriendshipReadModel>>(qualified);
    }

    [HttpGet("{id}")]
    public async Task<Response<FriendshipReadModel>> Show(SystemId id, CancellationToken ct)
    {
        var principal = PrincipalId;
        // RepresentsSameUserAs is the semantic self-check; a bare `principal == id`
        // byte compare would miss the raw route shape and let /api/friends/{rawId} slip
        // past this guard.
        if (principal.RepresentsSameUserAs(id))
        {
            return new ErrorResponse(
                "I'm pretty sure you don't count as your own friend. (Cannot view friendship status for self.)",
                ErrorCodes.CannotViewOwnFriendship,
                System.Net.HttpStatusCode.BadRequest);
        }

        var friendship = await _repository.GetFriendshipAsync(principal, id, ct);
        return friendship is null
            ? new ErrorResponse("You are not friends with that system.", ErrorCodes.FriendshipNotFound, System.Net.HttpStatusCode.NotFound)
            : QualifyFriendship(friendship);
    }

    private FriendshipReadModel QualifyFriendship(FriendshipReadModel friendship)
    {
        return friendship with
        {
            Friend = friendship.Friend with { AvatarUrl = QualifyAvatar(friendship.Friend.AvatarUrl, friendship.Friend.AvatarSource) },
            Fronting = friendship.Fronting
                .Select(f => f with { Alter = f.Alter with { AvatarUrl = QualifyAvatar(f.Alter.AvatarUrl, f.Alter.AvatarSource) } })
                .ToArray()
        };
    }

    [HttpDelete("{id}")]
    public async Task<Response> Delete(SystemId id, CancellationToken ct)
    {
        var principal = PrincipalId;
        // Semantic self-check — see Show handler for the same rationale.
        if (principal.RepresentsSameUserAs(id))
        {
            return new ErrorResponse(
                "I'm pretty sure you don't count as your own friend. (Cannot delete friendship with self.)",
                ErrorCodes.CannotDeleteOwnFriendship,
                System.Net.HttpStatusCode.BadRequest);
        }

        var envelope = new CommandEnvelope<RemoveFriendshipCommand>(
            OperationIds.FriendDelete,
            Guid.NewGuid(),
            PrincipalId: principal,
            IdempotencyKey: GetIdempotencyKey(),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new RemoveFriendshipCommand(id));

        return CommandNoContent(await _remove.HandleAsync(envelope, ct));
    }

    [HttpPost("{id}/trust")]
    public async Task<Response> Trust(SystemId id, CancellationToken ct)
        => await SetTrustInternal(id, true, OperationIds.FriendTrust, ErrorCodes.CannotTrustSelf, ct);

    [HttpPost("{id}/untrust")]
    public async Task<Response> Untrust(SystemId id, CancellationToken ct)
        => await SetTrustInternal(id, false, OperationIds.FriendUntrust, ErrorCodes.CannotUntrustSelf, ct);

    private async Task<Response> SetTrustInternal(
        SystemId id,
        bool trusted,
        OperationId operationId,
        ErrorCode selfErrorCode,
        CancellationToken ct)
    {
        var principal = PrincipalId;
        // Semantic self-check — see Show handler for the same rationale.
        if (principal.RepresentsSameUserAs(id))
        {
            return new ErrorResponse("Cannot trust self.", selfErrorCode, System.Net.HttpStatusCode.BadRequest);
        }

        var envelope = new CommandEnvelope<SetFriendTrustCommand>(
            operationId,
            Guid.NewGuid(),
            PrincipalId: principal,
            IdempotencyKey: GetIdempotencyKey(),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new SetFriendTrustCommand(id, trusted));

        return CommandNoContent(await _setTrust.HandleAsync(envelope, ct));
    }
}