namespace Interfold.Contracts.Models.Read;

/// <summary>Read model for <c>GET /api/systems/{systemId}/batch</c>. Emits
/// <c>{ friendship, tags, alters }</c>; guarded (visibility-filtered) projections
/// use <see cref="TagPublicReadModel"/> and <see cref="BareAlter"/>. Namespace
/// preserved as <c>Interfold.Contracts.Models.Read</c> for wire-compat; physically
/// migrated through spine → host/Interfold.Api/Models/ (Phase-3 Friendships slice) →
/// Interfold.Systems.Contracts (Phase-3 Systems facade slice — this file). Landed
/// here because PublicSystemsController.Batch is the sole consumer.</summary>
public sealed record PublicSystemBatchReadModel(
    FriendshipReadModel? Friendship,
    IReadOnlyList<TagPublicReadModel> Tags,
    IReadOnlyList<BareAlter> Alters
);
