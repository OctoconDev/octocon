using System.Diagnostics;
using Interfold.Api.Controllers.Base;
using Interfold.Api.Filters;
using Interfold.Api.Helpers;
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
        var model = await _accounts.GetPublicSystemAsync(systemId, ct);
        if (model is not null)
        {
            model = model with { AvatarUrl = QualifyAvatar(model) };
        }

        return OkOrNotFound(model, "System not found.", ErrorCodes.SystemNotFound);
    }

    [HttpGet("alters")]
    [SystemMustExist]
    public async Task<Response<IReadOnlyList<BareAlter>>> ListAlters([FromRoute] SystemId systemId, CancellationToken ct)
    {
        var alters = await _alters.ListGuardedAsync(systemId, PrincipalId, ct);
        foreach (var a in alters) a.AvatarUrl = QualifyAvatar(a);
        return new SuccessResponse<IReadOnlyList<BareAlter>>(alters);
    }

    [HttpGet("alters/{alterId:int}")]
    [SystemMustExist]
    public async Task<Response<BareAlter>> ShowAlter([FromRoute] SystemId systemId, [FromRoute][ValidAlterId] AlterId alterId, CancellationToken ct)
    {
        var alter = await _alters.GetGuardedAsync(systemId, alterId, PrincipalId, ct);
        if (alter is not null)
        {
            alter.AvatarUrl = QualifyAvatar(alter);
        }

        return OkOrNotFound(alter, "Alter not found.", ErrorCodes.AlterNotFound);
    }

    [HttpGet("tags")]
    [SystemMustExist]
    public async Task<Response<IReadOnlyList<TagPublicReadModel>>> ListTags([FromRoute] SystemId systemId, CancellationToken ct)
    {
        var tags = await _tags.ListGuardedAsync(systemId, PrincipalId, ct);
        return new SuccessResponse<IReadOnlyList<TagPublicReadModel>>(tags);
    }

    [HttpGet("tags/{tagId}")]
    [SystemMustExist]
    public async Task<Response<TagPublicReadModel>> ShowTag([FromRoute] SystemId systemId, [FromRoute] TagId tagId, CancellationToken ct)
    {
        var tag = await _tags.GetGuardedAsync(systemId, tagId, PrincipalId, ct);
        return OkOrNotFound(tag, "Tag not found.", ErrorCodes.TagNotFound);
    }

    [HttpGet("fronting")]
    [SystemMustExist]
    public async Task<Response<IReadOnlyList<FrontActiveReadModel>>> ListFronting([FromRoute] SystemId systemId, CancellationToken ct)
    {
        var fronts = await _fronting.ListActiveGuardedAsync(systemId, PrincipalId, ct);
        return new SuccessResponse<IReadOnlyList<FrontActiveReadModel>>(fronts);
    }

    [HttpGet("batch")]
    [SystemMustExist]
    public async Task<Response<PublicSystemBatchReadModel>> Batch([FromRoute] SystemId systemId, CancellationToken ct)
    {
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

        var aggregateSw = Stopwatch.StartNew();
        var altersTask = TimeSliceAsync("alter", _alters.ListGuardedAsync(systemId, principalId, ct));
        var tagsTask = TimeSliceAsync("tag", _tags.ListGuardedAsync(systemId, principalId, ct));
        var friendshipTask = _friendships.GetFriendshipAsync(principalId, systemId, ct);

        await Task.WhenAll(altersTask, tagsTask, friendshipTask);
        InterfoldMetrics.GuardedBatchLatencyMs.Record(aggregateSw.Elapsed.TotalMilliseconds,
            new KeyValuePair<string, object?>("slice", "aggregate"));

        var batchAlters = altersTask.Result;
        foreach (var a in batchAlters) a.AvatarUrl = QualifyAvatar(a);

        var friendship = friendshipTask.Result;
        if (friendship is not null)
        {
            // Inlined from the former InterfoldControllerBase.QualifyFriendship helper — the
            // shared spine wrapper moved out during the Phase-3 Friendships slice so
            // Interfold.Api.Shared no longer binds the friendship read-model cluster; per-avatar
            // qualification still uses the shared primitive.
            friendship = friendship with
            {
                Friend = friendship.Friend with
                {
                    AvatarUrl = AvatarUrlQualifier.QualifyAvatar(friendship.Friend, Request.Scheme, Request.Host),
                },
                Fronting = friendship.Fronting
                    .Select(f => f with
                    {
                        Alter = f.Alter with
                        {
                            AvatarUrl = AvatarUrlQualifier.QualifyAvatar(f.Alter, Request.Scheme, Request.Host),
                        },
                    })
                    .ToArray(),
            };
        }

        return new PublicSystemBatchReadModel(
            Friendship: friendship,
            Tags: tagsTask.Result,
            Alters: batchAlters);
    }

    // Records a per-slice latency sample for the batch endpoint's parallel branches.
    // Stopwatch is started before await so slice latency includes queueing when the
    // scheduler holds the task off the pool.
    private static async Task<T> TimeSliceAsync<T>(string slice, Task<T> sliceTask)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            return await sliceTask.ConfigureAwait(false);
        }
        finally
        {
            InterfoldMetrics.GuardedBatchLatencyMs.Record(sw.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("slice", slice));
        }
    }

}
