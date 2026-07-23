using Cassandra;
using Interfold.Shared.Contracts.Configuration;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Contracts.Models.Read;
using Interfold.Domain.Observability;
using Microsoft.Extensions.Logging;

namespace Interfold.Infrastructure.Scylla.Repository;

/// <summary>Query helpers shared by the viewer-aware read paths in the alter, tag, and
/// fronting repositories.</summary>
internal static class ScyllaSharedQueries
{
    /// <summary>Viewer's friendship level toward <paramref name="ownerSystemId"/>: null
    /// viewer → null, self-view → <see cref="FriendshipLevel.TrustedFriend"/>, otherwise
    /// the <c>global.friendships</c> row's level (null when not friends).</summary>
    public static async Task<FriendshipLevel?> ResolveFriendshipLevelAsync(
        ISession session,
        IScyllaKeyspaceResolver keyspaceResolver,
        SystemId ownerSystemId,
        SystemId? viewerSystemId,
        ILogger? logger = null)
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

        try
        {
            var query = new SimpleStatement(
                $"SELECT level FROM {ScyllaGlobalKeyspace.Name}.friendships WHERE user_id = ? AND friend_id = ? LIMIT 1",
                ownerSystemId.Value,
                normalizedViewerSystemId);

            var row = (await session.ExecuteAsync(query)).FirstOrDefault();
            return row is null ? null : row.GetValue<short>("level").FromCode<FriendshipLevel>();
        }
        catch (Exception ex)
        {
            GuardedMetrics.ErrorsTotal.Add(1,
                new KeyValuePair<string, object?>("entity_type", "friendship"),
                new KeyValuePair<string, object?>("exception_type", ex.GetType().Name));
            logger?.LogError(ex,
                "Guarded friendship lookup failed: owner={OwnerSystemId}, viewer={ViewerSystemId}",
                ownerSystemId.Value, normalizedViewerSystemId);
            throw;
        }
    }

    /// <summary>Joins stored field-value UDTs against the system's field definitions,
    /// emitting one entry per definition (null value when the alter didn't fill it in).</summary>
    public static IReadOnlyList<AlterPublicFieldReadModel> ResolveAlterFields(
        IEnumerable<AlterFieldUdt>? alterFields,
        IReadOnlyList<SettingsFieldReadModel> definitions)
    {
        if (definitions.Count == 0)
        {
            return [];
        }

        // Dict lookup — O(N + M), not O(N * M) like the previous LINQ FirstOrDefault.
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
