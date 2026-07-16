using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;
using Interfold.Contracts.Models;

namespace Interfold.Contracts.Models.Read;

/// <summary>
/// Read model for <c>GET /api/systems/{systemId}</c>. Wire-identical to the
/// anonymous <c>{ id, avatar_url, avatar_source, username, description }</c>
/// projection previously returned by <c>PublicSystemsController.Show</c>.
/// </summary>
/// <remarks>
/// The property names, when serialized under the global snake_case policy, map
/// exactly to the pre-typed shape:
///   <c>Id → "id"</c>,
///   <c>AvatarUrl → "avatar_url"</c>,
///   <c>AvatarSource → "avatar_source"</c>,
///   <c>Username → "username"</c>,
///   <c>Description → "description"</c>.
/// </remarks>
public sealed record PublicSystemReadModel(
    SystemId Id,
    AvatarUrl? AvatarUrl,
    AvatarSource? AvatarSource,
    Username? Username,
    string? Description
);

/// <summary>
/// Read model for <c>GET /api/systems/{systemId}/batch</c>. Wire-identical to
/// the anonymous <c>{ friendship, tags, alters }</c> shape. Tags use
/// <see cref="TagPublicReadModel"/> and alters use <see cref="BareAlter"/>
/// because guarded (visibility-filtered) reads project to those shapes.
/// </summary>
public sealed record PublicSystemBatchReadModel(
    FriendshipReadModel? Friendship,
    IReadOnlyList<TagPublicReadModel> Tags,
    IReadOnlyList<BareAlter> Alters
);
