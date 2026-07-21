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
        return row is null ? null : row.GetValue<short>("level").FromCode<FriendshipLevel>();
    }

    /// <summary>
    /// Joins an alter's stored field-value UDTs against the system's field definitions,
    /// emitting one entry per definition (null value when the alter hasn't filled it in).
    /// </summary>
    public static IReadOnlyList<AlterPublicFieldReadModel> ResolveAlterFields(
        IEnumerable<AlterFieldUdt>? alterFields,
        IReadOnlyList<SettingsFieldReadModel> definitions)
    {
        if (definitions.Count == 0)
        {
            return [];
        }

        // Materialise the stored UDTs into a dict keyed by field id so the per-definition
        // projection is O(N + M) rather than O(N * M) — matters once a system has more
        // than a handful of fields since the LINQ FirstOrDefault we used to run allocated
        // and scanned the sequence for every definition.
        var byFieldId = alterFields?.ToDictionary(x => x.Id, x => x.Value);
        return definitions
            .Select(def => new AlterPublicFieldReadModel(
                def.Id,
                def.Name,
                def.Type,
                byFieldId is not null && byFieldId.TryGetValue(def.Id, out var value) ? value : null))
            .ToArray();
    }

    public static async Task<AlterId?> LoadPrimaryFrontAlterAsync(ISession session, string keyspace, string normalizedSystemId)
    {
        var primaryRow = (await session.ExecuteAsync(new SimpleStatement(
            $"SELECT primary_front_alter FROM {keyspace}.users WHERE id = ? LIMIT 1",
            normalizedSystemId))).FirstOrDefault();

        return primaryRow?.GetValue<short?>("primary_front_alter") is { } primaryShort
            ? new AlterId(primaryShort)
            : null;
    }
}
