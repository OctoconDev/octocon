using Interfold.Api.Models;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models.Commands;
using Interfold.Shared.Contracts.Models.Read;
using Microsoft.AspNetCore.Mvc;
using Interfold.Shared.Contracts.Operations;
using Interfold.Shared.Domain.Abstractions.Repository;
using Interfold.Shared.Domain.Friendships;
using Interfold.Api.Controllers.Base;
using Interfold.Shared.Contracts;
using Interfold.Friendships.Api.Helpers;

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
        var qualified = friendships
            .Select(f => FriendshipAvatarQualifier.QualifyFriendship(f, Request.Scheme, Request.Host))
            .ToArray();
        return new SuccessResponse<IReadOnlyList<FriendshipReadModel>>(qualified);
    }


    [HttpGet("{id}")]
    public async Task<Response<FriendshipReadModel>> Show(SystemId id, CancellationToken ct)
    {
        // Semantic self-check — a bare byte compare would miss the raw route shape and
        // let /api/friends/{rawId} slip past this guard.
        if (RejectIfSelf(
                id,
                "I'm pretty sure you don't count as your own friend. (Cannot view friendship status for self.)",
                ErrorCodes.CannotViewOwnFriendship) is { } reject)
            return reject;

        var friendship = await _repository.GetFriendshipAsync(PrincipalId, id, ct);
        return OkOrNotFound(
            friendship is null ? null : FriendshipAvatarQualifier.QualifyFriendship(friendship, Request.Scheme, Request.Host),
            "You are not friends with that system.",
            ErrorCodes.FriendshipNotFound);
    }

    [HttpDelete("{id}")]
    public async Task<Response> Delete(SystemId id, CancellationToken ct)
    {
        // Semantic self-check — see Show handler for the same rationale.
        if (RejectIfSelf(
                id,
                "I'm pretty sure you don't count as your own friend. (Cannot delete friendship with self.)",
                ErrorCodes.CannotDeleteOwnFriendship) is { } reject)
            return reject;

        return await DispatchNoContentAsync(_remove, OperationIds.FriendDelete, new RemoveFriendshipCommand(id), ct);
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

        return await DispatchNoContentAsync(_setTrust, operationId, new SetFriendTrustCommand(id, trusted), ct);
    }
}
