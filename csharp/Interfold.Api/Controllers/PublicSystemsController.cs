using Interfold.Api.Controllers.Base;
using Interfold.Api.Models;
using Interfold.Contracts.Ids;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Read;
using Interfold.Domain.Abstractions.Repository;
using Microsoft.AspNetCore.Mvc;
using Interfold.Contracts;
using Interfold.Contracts.Validation;

namespace Interfold.Api.Controllers;

[Route("api/systems/{systemId}")]
public sealed class PublicSystemsController : InterfoldControllerBase
{
    private readonly IAccountRepository _accounts;
    private readonly IAlterRepository _alters;
    private readonly ITagRepository _tags;
    private readonly IFrontingRepository _fronting;
    private readonly IFriendshipRepository _friendships;

    public PublicSystemsController(
        IAccountRepository accounts,
        IAlterRepository alters,
        ITagRepository tags,
        IFrontingRepository fronting,
        IFriendshipRepository friendships)
    {
        _accounts = accounts;
        _alters = alters;
        _tags = tags;
        _fronting = fronting;
        _friendships = friendships;
    }

    [HttpGet]
    public async Task<Response<PublicSystemReadModel>> Show([FromRoute] SystemId systemId, CancellationToken ct)
    {
        var profile = await _accounts.GetPublicProfileAsync(systemId, ct);
        if (profile is null)
        {
            return new ErrorResponse("System not found.", ErrorCodes.SystemNotFound, System.Net.HttpStatusCode.NotFound);
        }

        return new PublicSystemReadModel(
            Id: profile.SystemId,
            AvatarUrl: QualifyAvatar(profile.AvatarUrl, profile.AvatarSource),
            AvatarSource: profile.AvatarSource,
            Username: profile.Username,
            Description: profile.Description);
    }

    //TODO: To ensure route works as expected
    [HttpGet("alters")]
    public async Task<Response<IReadOnlyList<BareAlter>>> ListAlters([FromRoute] SystemId systemId, CancellationToken ct)
    {
        if (!await SystemExistsAsync(systemId, ct))
        {
            return new ErrorResponse("System not found.", ErrorCodes.SystemNotFound, System.Net.HttpStatusCode.NotFound);
        }

        var alters = await _alters.ListGuardedAsync(systemId, PrincipalId, ct);
        foreach (var a in alters) a.AvatarUrl = QualifyAvatar(a.AvatarUrl, a.AvatarSource);
        return new SuccessResponse<IReadOnlyList<BareAlter>>(alters);
    }

    [HttpGet("alters/{alterId:int}")]
    public async Task<Response<BareAlter>> ShowAlter([FromRoute] SystemId systemId, [FromRoute][ValidAlterId] AlterId alterId, CancellationToken ct)
    {
        if (!await SystemExistsAsync(systemId, ct))
        {
            return new ErrorResponse("System not found.", ErrorCodes.SystemNotFound, System.Net.HttpStatusCode.NotFound);
        }

        var alter = await _alters.GetGuardedAsync(systemId, alterId, PrincipalId, ct);
        if (alter is not null)
        {
            alter.AvatarUrl = QualifyAvatar(alter.AvatarUrl, alter.AvatarSource);
        }

        return alter is null
            ? new ErrorResponse("Alter not found.", ErrorCodes.AlterNotFound, System.Net.HttpStatusCode.NotFound)
            : alter;
    }

    //TODO: To ensure route works as expected
    [HttpGet("tags")]
    public async Task<Response<IReadOnlyList<TagPublicReadModel>>> ListTags([FromRoute] SystemId systemId, CancellationToken ct)
    {
        if (!await SystemExistsAsync(systemId, ct))
        {
            return new ErrorResponse("System not found.", ErrorCodes.SystemNotFound, System.Net.HttpStatusCode.NotFound);
        }

        var tags = await _tags.ListGuardedAsync(systemId, PrincipalId, ct);
        return new SuccessResponse<IReadOnlyList<TagPublicReadModel>>(tags);
    }

    [HttpGet("tags/{tagId}")]
    public async Task<Response<TagPublicReadModel>> ShowTag([FromRoute] SystemId systemId, [FromRoute] TagId tagId, CancellationToken ct)
    {
        if (!await SystemExistsAsync(systemId, ct))
        {
            return new ErrorResponse("System not found.", ErrorCodes.SystemNotFound, System.Net.HttpStatusCode.NotFound);
        }

        var tag = await _tags.GetGuardedAsync(systemId, tagId, PrincipalId, ct);
        return tag is null
            ? new ErrorResponse("Tag not found.", ErrorCodes.TagNotFound, System.Net.HttpStatusCode.NotFound)
            : tag;
    }

    //TODO: To ensure route works as expected
    [HttpGet("fronting")]
    public async Task<Response<IReadOnlyList<FrontActiveReadModel>>> ListFronting([FromRoute] SystemId systemId, CancellationToken ct)
    {
        if (!await SystemExistsAsync(systemId, ct))
        {
            return new ErrorResponse("System not found.", ErrorCodes.SystemNotFound, System.Net.HttpStatusCode.NotFound);
        }

        var fronts = await _fronting.ListActiveGuardedAsync(systemId, PrincipalId, ct);
        return new SuccessResponse<IReadOnlyList<FrontActiveReadModel>>(fronts);
    }

    [HttpGet("batch")]
    public async Task<Response<PublicSystemBatchReadModel>> Batch([FromRoute] SystemId systemId, CancellationToken ct)
    {
        if (!await SystemExistsAsync(systemId, ct))
        {
            return new ErrorResponse("System not found.", ErrorCodes.SystemNotFound, System.Net.HttpStatusCode.NotFound);
        }

        var principalId = PrincipalId;
        // Semantic self-check. RepresentsSameUserAs compares scoped-to-scoped when both
        // sides carry a region prefix, so a cross-region collision (same raw id, different
        // region → different user) is correctly treated as a non-self request.
        if (principalId.RepresentsSameUserAs(systemId))
        {
            return new ErrorResponse(
                "You cannot view your own system through this endpoint.",
                ErrorCodes.InvalidEndpoint,
                System.Net.HttpStatusCode.Forbidden);
        }

        var altersTask = _alters.ListGuardedAsync(systemId, principalId, ct);
        var tagsTask = _tags.ListGuardedAsync(systemId, principalId, ct);
        var friendshipTask = _friendships.GetFriendshipAsync(principalId, systemId, ct);

        await Task.WhenAll(altersTask, tagsTask, friendshipTask);

        var batchAlters = altersTask.Result;
        foreach (var a in batchAlters) a.AvatarUrl = QualifyAvatar(a.AvatarUrl, a.AvatarSource);

        var friendship = friendshipTask.Result;
        if (friendship is not null)
        {
            friendship = friendship with
            {
                Friend = friendship.Friend with { AvatarUrl = QualifyAvatar(friendship.Friend.AvatarUrl, friendship.Friend.AvatarSource) },
                Fronting = friendship.Fronting
                    .Select(f => f with { Alter = f.Alter with { AvatarUrl = QualifyAvatar(f.Alter.AvatarUrl, f.Alter.AvatarSource) } })
                    .ToList()
            };
        }

        return new PublicSystemBatchReadModel(
            Friendship: friendship,
            Tags: tagsTask.Result,
            Alters: batchAlters);
    }

    private async Task<bool> SystemExistsAsync(SystemId systemId, CancellationToken ct)
    {
        var profile = await _accounts.GetPublicProfileAsync(systemId, ct);
        return profile is not null;
    }
}
