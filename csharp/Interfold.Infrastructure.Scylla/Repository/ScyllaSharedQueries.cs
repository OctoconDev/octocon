using Cassandra;
using Interfold.Contracts.Configuration;
using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Read;

namespace Interfold.Infrastructure.Scylla.Repository;

/// <summary>
/// Query/mapping helpers shared by the guarded (viewer-aware) read paths of the alter,
/// tag, and fronting repositories — previously byte-identical private copies in each.
/// </summary>
internal static class ScyllaSharedQueries
{
    /// <summary>
    /// Resolves the viewer's friendship level toward <paramref name="ownerSystemId"/>:
    /// null viewer → null (public-only), self-view → <see cref="FriendshipLevel.TrustedFriend"/>,
    /// otherwise the <c>global.friendships</c> row's level (null when not friends).
    /// </summary>
    public static async Task<FriendshipLevel?> ResolveFriendshipLevelAsync(
        ISession session,
        IScyllaKeyspaceResolver keyspaceResolver,
        SystemId ownerSystemId,
        SystemId? viewerSystemId)
    {
        if (string.IsNullOrWhiteSpace(viewerSystemId?.Value))
        {
            return null;
        }

        var normalizedViewerSystemId = keyspaceResolver.NormalizeSystemId(viewerSystemId.Value);
        if (keyspaceResolver.NormalizeSystemId(ownerSystemId) == normalizedViewerSystemId)
        {
            return FriendshipLevel.TrustedFriend;
        }

        var query = new SimpleStatement(
            $"SELECT level FROM {ScyllaGlobalKeyspace.Name}.friendships WHERE user_id = ? AND friend_id = ? LIMIT 1",
            ownerSystemId.Value,
            normalizedViewerSystemId);

        var row = (await session.ExecuteAsync(query)).FirstOrDefault();
        return row is null ? null : row.GetValue<short>("level").FromCode(FriendshipLevel.Friend);
    }

    /// <summary>
    /// Joins an alter's stored field-value UDTs against the system's field definitions,
    /// emitting one entry per definition (null value when the alter hasn't filled it in).
    /// </summary>
    public static IReadOnlyList<AlterPublicFieldReadModel> ResolveAlterFields(
        IEnumerable<ScyllaAlterRepository.AlterFieldUdt>? alterFields,
        IReadOnlyList<SettingsFieldReadModel> definitions)
    {
        if (definitions.Count == 0)
        {
            return [];
        }

        return definitions
            .Select(def => new AlterPublicFieldReadModel(def.Id, def.Name, def.Type, alterFields?.FirstOrDefault(x => x.Id == def.Id)?.Value))
            .ToArray();
    }
}
