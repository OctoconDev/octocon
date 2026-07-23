using System.Diagnostics;
using Cassandra;
using Interfold.Shared.Contracts.Configuration;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Contracts.Models.Commands;
using Interfold.Shared.Contracts.Models.Read;
using Interfold.Shared.Domain.Abstractions.Repository;
using Interfold.Shared.Domain.Observability;
using Interfold.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Interfold.Infrastructure.Scylla.Repository;

public sealed class ScyllaTagRepository : ITagRepository
{
    private readonly IScyllaSessionProvider _sessionProvider;
    private readonly IScyllaScopeResolver _scopeResolver;
    private readonly IScyllaKeyspaceResolver _keyspaceResolver;
    private readonly PersistenceConfiguration _options;
    private readonly IAlterRepository _alterRepository;
    private readonly ILogger<ScyllaTagRepository> _logger;

    public ScyllaTagRepository(
        IScyllaSessionProvider sessionProvider,
        IScyllaScopeResolver scopeResolver,
        IScyllaKeyspaceResolver keyspaceResolver,
        IOptions<PersistenceConfiguration> options,
        IAlterRepository alterRepository,
        ILogger<ScyllaTagRepository> logger
    )
    {
        _sessionProvider = sessionProvider;
        _scopeResolver = scopeResolver;
        _keyspaceResolver = keyspaceResolver;
        _options = options.Value;
        _alterRepository = alterRepository;
        _logger = logger;
    }

    public async Task<TagId?> CreateAsync(
        SystemId systemId,
        CreateTagCommand command,
        CancellationToken cancellationToken = default
    )
    {
        return await _scopeResolver.ExecuteAsync<TagId?>(systemId, async scope =>
        {
            var (session, keyspace, normalizedSystemId) = scope;
            Guid? parentTagId = null;

            if (command.ParentTagId is { } requestedParentTagId && requestedParentTagId != TagId.Empty)
            {
                parentTagId = requestedParentTagId.Value;
            }

            if (parentTagId is not null)
            {
                var parentCheck = new SimpleStatement(
                    $"SELECT id FROM {keyspace}.tags WHERE user_id = ? AND id = ? LIMIT 1",
                    normalizedSystemId,
                    parentTagId
                );

                var parentRows = await session.ExecuteAsync(parentCheck);
                if (!parentRows.Any())
                    return null;
            }

            var tagGuid = Guid.NewGuid();

            // Stamp security_level so read-back never sees null — same invariant as ScyllaAlterRepository.CreateAsync / ScyllaAlterRepositoryUdtNullTests.
            var insert = new SimpleStatement(
                $"INSERT INTO {keyspace}.tags (user_id, id, parent_tag_id, name, description, color, security_level, inserted_at, updated_at) VALUES (?, ?, ?, ?, ?, ?, ?, ?, toTimestamp(now()))",
                normalizedSystemId,
                tagGuid,
                parentTagId,
                command.Name,
                null,
                null,
                (short)VisibilityLevel.Private,
                command.InsertedAtUtc
            );

            await session.ExecuteAsync(insert);
            return new(tagGuid);
        }, cancellationToken);
    }

    public async Task<bool> ExistsAsync(
        SystemId systemId,
        TagId tagId,
        CancellationToken cancellationToken = default
    )
    {
        return await _scopeResolver.ExecuteAsync(systemId, scope =>
            ExistsAsync(scope, tagId),
            cancellationToken);
    }

    private static Task<bool> ExistsAsync(ScyllaScope scope, TagId tagId)
        => ScyllaExistsQueries.RowExistsAsync(scope.Session, scope.Keyspace, "tags", "id", scope.NormalizedSystemId, tagId.Value);

    public async Task<bool> UpdateAsync(
        SystemId systemId,
        UpdateTagCommand command,
        CancellationToken cancellationToken = default
    )
    {
        return await _scopeResolver.ExecuteAsync(systemId, async scope =>
        {
            var (session, keyspace, normalizedSystemId) = scope;

            var exists = await ExistsAsync(scope, command.TagId);
            if (!exists)
            {
                return false;
            }

            var setClauses = new List<string>();
            var values = new List<object?>();

            if (command.Name is not null)
            {
                setClauses.Add("name = ?");
                values.Add(command.Name);
            }

            if (command.Color is { } color)
            {
                setClauses.Add("color = ?");
                values.Add(color.Value);
            }

            if (command.Description is not null)
            {
                setClauses.Add("description = ?");
                values.Add(command.Description);
            }

            if (command.SecurityLevel is not null)
            {
                setClauses.Add("security_level = ?");
                values.Add((short)command.SecurityLevel.Value);
            }

            if (setClauses.Count == 0)
            {
                return true;
            }

            setClauses.Add("updated_at = toTimestamp(now())");
            values.Add(normalizedSystemId);
            values.Add(command.TagId.Value);

            var update = new SimpleStatement(
                $"UPDATE {keyspace}.tags SET {string.Join(", ", setClauses)} WHERE user_id = ? AND id = ?",
                [.. values]);

            await session.ExecuteAsync(update);

            return true;
        }, cancellationToken);
    }

    public async Task<bool> DeleteAsync(SystemId systemId, TagId tagId, CancellationToken cancellationToken = default)
    {
        return await _scopeResolver.ExecuteAsync(systemId, async scope =>
        {
            var (session, keyspace, normalizedSystemId) = scope;

            var exists = await ExistsAsync(scope, tagId);
            if (!exists)
            {
                return false;
            }

            var deleteBatch = new BatchStatement();
            deleteBatch.Add(new SimpleStatement(
                $"DELETE FROM {keyspace}.tags WHERE user_id = ? AND id = ?",
                normalizedSystemId,
                tagId.Value
            ));
            deleteBatch.Add(new SimpleStatement(
                $"DELETE FROM {keyspace}.alter_tags WHERE user_id = ? AND tag_id = ?",
                normalizedSystemId,
                tagId.Value
            ));

            // Clean up alter_tags_by_alter: find all alters with this tag and remove reverse entries
            var tagAlters = await session.ExecuteAsync(new SimpleStatement(
                $"SELECT alter_id FROM {keyspace}.alter_tags WHERE user_id = ? AND tag_id = ?",
                normalizedSystemId, tagId.Value));
            foreach (var tagAlterRow in tagAlters)
            {
                var aid = tagAlterRow.GetValue<short>("alter_id");
                deleteBatch.Add(new SimpleStatement(
                    $"DELETE FROM {keyspace}.alter_tags_by_alter WHERE user_id = ? AND alter_id = ? AND tag_id = ?",
                    normalizedSystemId, aid, tagId.Value));
            }

            await session.ExecuteAsync(deleteBatch);

            return true;
        }, cancellationToken);
    }

    public async Task<bool> AttachAlterAsync(
        SystemId systemId,
        TagId tagId,
        AlterId alterId,
        CancellationToken cancellationToken = default
    )
    {
        return await _scopeResolver.ExecuteAsync(systemId, async scope =>
        {
            var (session, keyspace, normalizedSystemId) = scope;

            var tagExists = await ExistsAsync(scope, tagId);
            if (!tagExists)
            {
                return false;
            }

            var insert = new BatchStatement();
            insert.Add(new SimpleStatement(
                $"INSERT INTO {keyspace}.alter_tags (user_id, tag_id, alter_id, inserted_at, updated_at) VALUES (?, ?, ?, toTimestamp(now()), toTimestamp(now()))",
                normalizedSystemId,
                tagId.Value,
                alterId.Value
            ));
            insert.Add(new SimpleStatement(
                $"INSERT INTO {keyspace}.alter_tags_by_alter (user_id, alter_id, tag_id, inserted_at, updated_at) VALUES (?, ?, ?, toTimestamp(now()), toTimestamp(now()))",
                normalizedSystemId,
                alterId.Value,
                tagId.Value
            ));
            await session.ExecuteAsync(insert);

            return true;
        }, cancellationToken);
    }

    public async Task<bool> DetachAlterAsync(
        SystemId systemId,
        TagId tagId,
        AlterId alterId,
        CancellationToken cancellationToken = default
    )
    {
        return await _scopeResolver.ExecuteAsync(systemId, async scope =>
        {
            var (session, keyspace, normalizedSystemId) = scope;

            var edgeExistsQuery = new SimpleStatement(
                $"SELECT alter_id FROM {keyspace}.alter_tags WHERE user_id = ? AND tag_id = ? AND alter_id = ? LIMIT 1",
                normalizedSystemId,
                tagId.Value,
                alterId.Value
            );

            var edgeRows = await session.ExecuteAsync(edgeExistsQuery);
            if (!edgeRows.Any())
            {
                return false;
            }

            var delete = new BatchStatement();
            delete.Add(new SimpleStatement(
                $"DELETE FROM {keyspace}.alter_tags WHERE user_id = ? AND tag_id = ? AND alter_id = ?",
                normalizedSystemId,
                tagId.Value,
                alterId.Value
            ));
            delete.Add(new SimpleStatement(
                $"DELETE FROM {keyspace}.alter_tags_by_alter WHERE user_id = ? AND alter_id = ? AND tag_id = ?",
                normalizedSystemId,
                alterId.Value,
                tagId.Value
            ));
            await session.ExecuteAsync(delete);

            return true;
        }, cancellationToken);
    }

    public async Task<TagId?> GetParentIdAsync(
        SystemId systemId,
        TagId tagId,
        CancellationToken cancellationToken = default
    )
    {
        return await _scopeResolver.ExecuteAsync<TagId?>(systemId, async scope =>
        {
            var (session, keyspace, normalizedSystemId) = scope;

            var query = new SimpleStatement(
                $"SELECT parent_tag_id FROM {keyspace}.tags WHERE user_id = ? AND id = ? LIMIT 1",
                normalizedSystemId,
                tagId.Value
            );

            var row = (await session.ExecuteAsync(query)).FirstOrDefault();
            return ToTagId(row?.GetValue<Guid?>("parent_tag_id"));
        }, cancellationToken);
    }

    public async Task<bool> SetParentAsync(
        SystemId systemId,
        TagId tagId,
        TagId parentTagId,
        CancellationToken cancellationToken = default
    )
    {
        return await _scopeResolver.ExecuteAsync(systemId, async scope =>
        {
            var (session, keyspace, normalizedSystemId) = scope;

            var childExists = await ExistsAsync(scope, tagId);
            var parentExists = await ExistsAsync(scope, parentTagId);
            if (!childExists || !parentExists)
            {
                return false;
            }

            var update = new SimpleStatement(
                $"UPDATE {keyspace}.tags SET parent_tag_id = ?, updated_at = toTimestamp(now()) WHERE user_id = ? AND id = ?",
                parentTagId.Value,
                normalizedSystemId,
                tagId.Value
            );
            await session.ExecuteAsync(update);

            return true;
        }, cancellationToken);
    }

    public async Task<bool> RemoveParentAsync(
        SystemId systemId,
        TagId tagId,
        CancellationToken cancellationToken = default
    )
    {
        return await _scopeResolver.ExecuteAsync(systemId, async scope =>
        {
            var (session, keyspace, normalizedSystemId) = scope;

            var exists = await ExistsAsync(scope, tagId);
            if (!exists)
            {
                return false;
            }

            var update = new SimpleStatement(
                $"UPDATE {keyspace}.tags SET parent_tag_id = ?, updated_at = toTimestamp(now()) WHERE user_id = ? AND id = ?",
                null,
                normalizedSystemId,
                tagId.Value
            );
            await session.ExecuteAsync(update);

            return true;
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<TagReadModel>> ListAsync(SystemId systemId, CancellationToken cancellationToken = default)
    {
        return await _scopeResolver.ExecuteAsync(systemId, async scope =>
        {
            var (session, keyspace, normalizedSystemId) = scope;

            var query = new SimpleStatement(
                $"SELECT id, name, color, description, parent_tag_id, inserted_at, updated_at, security_level, user_id FROM {keyspace}.tags WHERE user_id = ?",
                normalizedSystemId
            );

            var rows = await session.ExecuteAsync(query);
            var tags = new List<TagReadModel>();

            foreach (var row in rows)
            {
                var alterIds = await GetAlterIdsAsync(session, keyspace, normalizedSystemId, row.GetValue<Guid>("id"));
                tags.Add(TagRowMappers.MapTagReadModel(row, alterIds));
            }

            // VERIFIED: 2026-03-17 Elixir tags.ex get_tags() has no explicit sort → database order (ascending). Matches C# OrderBy.
            // Sort key is the wire form (lowercase "N" hex) to keep list ordering byte-identical
            // to the historic string-backed TagId — Guid.CompareTo bytewise reorders differently.
            return tags.OrderBy(x => x.Id.Value.ToString("N"), StringComparer.Ordinal).ToArray();
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<TagPublicReadModel>> ListGuardedAsync(
        SystemId systemId,
        SystemId? viewerSystemId,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var result = await _scopeResolver.ExecuteAsync(systemId, async scope =>
        {
            var (session, keyspace, normalizedSystemId) = scope;
            var friendshipLevel = await ScyllaSharedQueries.ResolveFriendshipLevelAsync(session, _keyspaceResolver, new(normalizedSystemId), viewerSystemId, _logger);
            var hydrationConcurrency = _options.HydrationMaxConcurrency;

            var query = new SimpleStatement(
                $"SELECT id, name, color, description, parent_tag_id, inserted_at, updated_at, security_level, user_id FROM {keyspace}.tags WHERE user_id = ?",
                normalizedSystemId
            );

            var rows = (await session.ExecuteAsync(query)).ToArray();
            var totalCount = rows.Length;
            var tags = new List<TagPublicReadModel>();

            foreach (var row in rows)
            {
                var visibility = row.GetValue<short?>("security_level").FromCode<VisibilityLevel>();
                if (!visibility.CanBeViewedBy(friendshipLevel))
                {
                    continue;
                }

                var alterIds = await GetGuardedAlterIdsAsync(session, keyspace, normalizedSystemId, row.GetValue<Guid>("id"), friendshipLevel);
                var alters = alterIds.Count == 0
                    ? Array.Empty<BareAlter>()
                    : (await ConcurrentProjection.SelectWithConcurrencyAsync(
                            alterIds,
                            hydrationConcurrency,
                            id => _alterRepository.GetGuardedAsync(systemId, id, viewerSystemId, cancellationToken),
                            cancellationToken))
                        .Where(x => x != null)
                        .ToArray();
                tags.Add(TagRowMappers.MapTagPublicReadModel(row, alters!));
            }

            var ordered = tags.OrderBy(x => x.Id.Value.ToString("N"), StringComparer.Ordinal).ToArray();
            return (Total: totalCount, Visible: (IReadOnlyList<TagPublicReadModel>)ordered, NormalizedSystemId: normalizedSystemId);
        }, cancellationToken);

        GuardedInstrumentation.RecordList(_logger, "tag", nameof(ListGuardedAsync), viewerSystemId, result.NormalizedSystemId, result.Total, result.Visible.Count, sw.Elapsed.TotalMilliseconds);
        return result.Visible;
    }

    public async Task<TagReadModel?> GetAsync(SystemId systemId, TagId tagId, CancellationToken cancellationToken = default)
    {
        return await _scopeResolver.ExecuteAsync(systemId, async scope =>
        {
            var (session, keyspace, normalizedSystemId) = scope;

            var query = new SimpleStatement(
                $"SELECT id, name, color, description, parent_tag_id, inserted_at, updated_at, security_level, user_id FROM {keyspace}.tags WHERE user_id = ? AND id = ? LIMIT 1",
                normalizedSystemId,
                tagId.Value
            );

            var row = (await session.ExecuteAsync(query)).FirstOrDefault();
            if (row is null)
            {
                return null;
            }

            var alterIds = await GetAlterIdsAsync(session, keyspace, normalizedSystemId, tagId.Value);           
            return TagRowMappers.MapTagReadModel(row, alterIds);
        }, cancellationToken);
    }

    public async Task<TagPublicReadModel?> GetGuardedAsync(
        SystemId systemId,
        TagId tagId,
        SystemId? viewerSystemId,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var result = await _scopeResolver.ExecuteAsync(systemId, async scope =>
        {
            var (session, keyspace, normalizedSystemId) = scope;
            var friendshipLevel = await ScyllaSharedQueries.ResolveFriendshipLevelAsync(session, _keyspaceResolver, new(normalizedSystemId), viewerSystemId, _logger);
            var hydrationConcurrency = _options.HydrationMaxConcurrency;

            var query = new SimpleStatement(
                $"SELECT id, name, color, description, parent_tag_id, inserted_at, updated_at, security_level, user_id FROM {keyspace}.tags WHERE user_id = ? AND id = ? LIMIT 1",
                normalizedSystemId,
                tagId.Value
            );

            var row = (await session.ExecuteAsync(query)).FirstOrDefault();
            if (row is null)
            {
                return (Tag: (TagPublicReadModel?)null, Filtered: false, NormalizedSystemId: normalizedSystemId);
            }

            var visibility = row.GetValue<short?>("security_level").FromCode<VisibilityLevel>();
            if (!visibility.CanBeViewedBy(friendshipLevel))
            {
                return (Tag: (TagPublicReadModel?)null, Filtered: true, NormalizedSystemId: normalizedSystemId);
            }

            var alterIds = await GetGuardedAlterIdsAsync(session, keyspace, normalizedSystemId, tagId.Value, friendshipLevel);
            var alters = alterIds.Count == 0
                ? Array.Empty<BareAlter>()
                : (await ConcurrentProjection.SelectWithConcurrencyAsync(
                        alterIds,
                        hydrationConcurrency,
                        id => _alterRepository.GetGuardedAsync(systemId, id, viewerSystemId, cancellationToken),
                        cancellationToken))
                    .Where(x => x != null)
                    .ToArray();
            return (Tag: (TagPublicReadModel?)TagRowMappers.MapTagPublicReadModel(row, alters!), Filtered: false, NormalizedSystemId: normalizedSystemId);
        }, cancellationToken);

        GuardedInstrumentation.RecordGet(_logger, "tag", nameof(GetGuardedAsync), viewerSystemId, result.NormalizedSystemId, tagId.Value.ToString("N"), found: result.Tag is not null, filtered: result.Filtered, sw.Elapsed.TotalMilliseconds);
        return result.Tag;
    }

    private static async Task<IReadOnlyList<AlterId>> GetAlterIdsAsync(ISession session, string keyspace, string normalizedSystemId, Guid tagId)
    {
        var query = new SimpleStatement(
            $"SELECT alter_id FROM {keyspace}.alter_tags WHERE user_id = ? AND tag_id = ?",
            normalizedSystemId,
            tagId
        );

        var rows = await session.ExecuteAsync(query);
        return rows.Select(x => new AlterId(x.GetValue<short>("alter_id"))).OrderBy(x => x.Value).ToArray();
    }

    private static async Task<IReadOnlyList<AlterId>> GetGuardedAlterIdsAsync(
        ISession session,
        string keyspace,
        string normalizedSystemId,
        Guid tagId,
        FriendshipLevel? friendshipLevel)
    {
        var alterIds = await GetAlterIdsAsync(session, keyspace, normalizedSystemId, tagId);
        if (alterIds.Count == 0)
        {
            return alterIds;
        }

        var query = new SimpleStatement(
            $"SELECT id, security_level FROM {keyspace}.alters WHERE user_id = ?",
            normalizedSystemId);

        var rows = await session.ExecuteAsync(query);
        var visible = rows
            .Select(row => new
            {
                AlterId = new AlterId(row.GetValue<short>("id")),
                Visibility = row.GetValue<short?>("security_level").FromCode<VisibilityLevel>()
            })
            .Where(x => alterIds.Contains(x.AlterId) && x.Visibility.CanBeViewedBy(friendshipLevel))
            .Select(x => x.AlterId)
            .OrderBy(x => x.Value)
            .ToArray();

        return visible;
    }

    private static TagId? ToTagId(Guid? guid)
        => guid is null ? null : new TagId(guid.Value);
}
