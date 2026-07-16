using Cassandra;
using Interfold.Contracts.Configuration;
using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Models.Read;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Infrastructure.Persistence;
using Microsoft.Extensions.Options;

namespace Interfold.Infrastructure.Scylla.Repository;

public sealed class ScyllaTagRepository : ITagRepository
{
    private readonly IScyllaSessionProvider _sessionProvider;
    private readonly IScyllaKeyspaceResolver _keyspaceResolver;
    private readonly PersistenceConfiguration _options;
    private readonly IAlterRepository _alterRepository;

    public ScyllaTagRepository(
        IScyllaSessionProvider sessionProvider,
        IScyllaKeyspaceResolver keyspaceResolver,
        IOptions<PersistenceConfiguration> options,
        IAlterRepository alterRepository
    )
    {
        _sessionProvider = sessionProvider;
        _keyspaceResolver = keyspaceResolver;
        _options = options.Value;
        _alterRepository = alterRepository;
    }

    public async Task<TagId?> CreateAsync(
        SystemId systemId,
        CreateTagCommand command,
        CancellationToken cancellationToken = default
    )
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync<TagId?>(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);
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

            var insert = new SimpleStatement(
                $"INSERT INTO {keyspace}.tags (user_id, id, parent_tag_id, name, description, color, security_level, inserted_at, updated_at) VALUES (?, ?, ?, ?, ?, ?, ?, ?, toTimestamp(now()))",
                normalizedSystemId,
                tagGuid,
                parentTagId,
                command.Name,
                null,
                null,
                null,
                command.InsertedAtUtc
            );

            await session.ExecuteAsync(insert);
            return new(tagGuid);
        }, _options, cancellationToken);
    }

    public async Task<bool> ExistsAsync(
        SystemId systemId,
        TagId tagId,
        CancellationToken cancellationToken = default
    )
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            var query = new SimpleStatement(
                $"SELECT id FROM {keyspace}.tags WHERE user_id = ? AND id = ? LIMIT 1",
                normalizedSystemId,
                tagId.Value
            );

            var rows = await session.ExecuteAsync(query);
            return rows.Any();
        }, _options, cancellationToken);
    }

    public async Task<bool> UpdateAsync(
        SystemId systemId,
        UpdateTagCommand command,
        CancellationToken cancellationToken = default
    )
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            var exists = await ExistsAsync(systemId, command.TagId, cancellationToken);
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
        }, _options, cancellationToken);
    }

    public async Task<bool> DeleteAsync(SystemId systemId, TagId tagId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            var exists = await ExistsAsync(systemId, tagId, cancellationToken);
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
        }, _options, cancellationToken);
    }

    public async Task<bool> AttachAlterAsync(
        SystemId systemId,
        TagId tagId,
        AlterId alterId,
        CancellationToken cancellationToken = default
    )
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            var tagExists = await ExistsAsync(systemId, tagId, cancellationToken);
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
        }, _options, cancellationToken);
    }

    public async Task<bool> DetachAlterAsync(
        SystemId systemId,
        TagId tagId,
        AlterId alterId,
        CancellationToken cancellationToken = default
    )
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

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
        }, _options, cancellationToken);
    }

    public async Task<TagId?> GetParentIdAsync(
        SystemId systemId,
        TagId tagId,
        CancellationToken cancellationToken = default
    )
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync<TagId?>(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            var query = new SimpleStatement(
                $"SELECT parent_tag_id FROM {keyspace}.tags WHERE user_id = ? AND id = ? LIMIT 1",
                normalizedSystemId,
                tagId.Value
            );

            var row = (await session.ExecuteAsync(query)).FirstOrDefault();
            return ToTagId(row?.GetValue<Guid?>("parent_tag_id"));
        }, _options, cancellationToken);
    }

    public async Task<bool> SetParentAsync(
        SystemId systemId,
        TagId tagId,
        TagId parentTagId,
        CancellationToken cancellationToken = default
    )
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            var childExists = await ExistsAsync(systemId, tagId, cancellationToken);
            var parentExists = await ExistsAsync(systemId, parentTagId, cancellationToken);
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
        }, _options, cancellationToken);
    }

    public async Task<bool> RemoveParentAsync(
        SystemId systemId,
        TagId tagId,
        CancellationToken cancellationToken = default
    )
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            var exists = await ExistsAsync(systemId, tagId, cancellationToken);
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
        }, _options, cancellationToken);
    }

    public async Task<IReadOnlyList<TagReadModel>> ListAsync(SystemId systemId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            var query = new SimpleStatement(
                $"SELECT id, name, color, description, parent_tag_id, inserted_at, updated_at, security_level, user_id FROM {keyspace}.tags WHERE user_id = ?",
                normalizedSystemId
            );

            var rows = await session.ExecuteAsync(query);
            var tags = new List<TagReadModel>();

            foreach (var row in rows)
            {
                TagId tagId = new(row.GetValue<Guid>("id"));
                var alterIds = await GetAlterIdsAsync(session, keyspace, normalizedSystemId, row.GetValue<Guid>("id"));
                tags.Add(new TagReadModel(
                    tagId,
                    row.GetValue<string>("name"),
                    HexColor.FromNullable(row.GetValue<string?>("color")),
                    row.GetValue<string?>("description"),
                    ToTagId(row.GetValue<Guid?>("parent_tag_id")),
                    alterIds,
                    row.GetValue<DateTimeOffset>("inserted_at").UtcDateTime,
                    row.GetValue<DateTimeOffset>("updated_at").UtcDateTime,
                    row.GetValue<short?>("security_level").FromCode(VisibilityLevel.Public),
                    new(row.GetValue<string>("user_id"))));
            }

            // VERIFIED: 2026-03-17 Elixir tags.ex get_tags() has no explicit sort → database order (ascending). Matches C# OrderBy.
            // Sort key is the wire form (lowercase "N" hex) to keep list ordering byte-identical
            // to the historic string-backed TagId — Guid.CompareTo bytewise reorders differently.
            return tags.OrderBy(x => x.Id.Value.ToString("N"), StringComparer.Ordinal).ToArray();
        }, _options, cancellationToken);
    }

    public async Task<IReadOnlyList<TagPublicReadModel>> ListGuardedAsync(
        SystemId systemId,
        SystemId? viewerSystemId,
        CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);
            var friendshipLevel = await ScyllaSharedQueries.ResolveFriendshipLevelAsync(session, _keyspaceResolver, new(normalizedSystemId), viewerSystemId);
            var hydrationConcurrency = _options.HydrationMaxConcurrency;

            var query = new SimpleStatement(
                $"SELECT id, name, color, description, parent_tag_id, inserted_at, updated_at, security_level, user_id FROM {keyspace}.tags WHERE user_id = ?",
                normalizedSystemId
            );

            var rows = await session.ExecuteAsync(query);
            var tags = new List<TagPublicReadModel>();

            foreach (var row in rows)
            {
                var visibility = row.GetValue<short?>("security_level").FromCode(VisibilityLevel.Public);
                if (!visibility.CanBeViewedBy(friendshipLevel))
                {
                    continue;
                }

                TagId tagId = new(row.GetValue<Guid>("id"));
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
                tags.Add(new TagPublicReadModel(
                    tagId,
                    row.GetValue<string>("name"),
                    HexColor.FromNullable(row.GetValue<string?>("color")),
                    row.GetValue<string?>("description"),
                    ToTagId(row.GetValue<Guid?>("parent_tag_id")),
                    alters!,
                    row.GetValue<DateTimeOffset>("inserted_at").UtcDateTime,
                    row.GetValue<DateTimeOffset>("updated_at").UtcDateTime,
                    visibility,
                    new(row.GetValue<string>("user_id"))));
            }

            return tags.OrderBy(x => x.Id.Value.ToString("N"), StringComparer.Ordinal).ToArray();
        }, _options, cancellationToken);
    }

    public async Task<TagReadModel?> GetAsync(SystemId systemId, TagId tagId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

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
            return new TagReadModel(
                new(row.GetValue<Guid>("id")),
                row.GetValue<string>("name"),
                HexColor.FromNullable(row.GetValue<string?>("color")),
                row.GetValue<string?>("description"),
                ToTagId(row.GetValue<Guid?>("parent_tag_id")),
                alterIds,
                row.GetValue<DateTimeOffset>("inserted_at").UtcDateTime,
                row.GetValue<DateTimeOffset>("updated_at").UtcDateTime,
                row.GetValue<short?>("security_level").FromCode(VisibilityLevel.Public),
                new(row.GetValue<string>("user_id")));
        }, _options, cancellationToken);
    }

    public async Task<TagPublicReadModel?> GetGuardedAsync(
        SystemId systemId,
        TagId tagId,
        SystemId? viewerSystemId,
        CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);
            var friendshipLevel = await ScyllaSharedQueries.ResolveFriendshipLevelAsync(session, _keyspaceResolver, new(normalizedSystemId), viewerSystemId);
            var hydrationConcurrency = _options.HydrationMaxConcurrency;

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

            var visibility = row.GetValue<short?>("security_level").FromCode(VisibilityLevel.Public);
            if (!visibility.CanBeViewedBy(friendshipLevel))
            {
                return null;
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
            return new TagPublicReadModel(
                new(row.GetValue<Guid>("id")),
                row.GetValue<string>("name"),
                HexColor.FromNullable(row.GetValue<string?>("color")),
                row.GetValue<string?>("description"),
                ToTagId(row.GetValue<Guid?>("parent_tag_id")),
                alters!,
                row.GetValue<DateTimeOffset>("inserted_at").UtcDateTime,
                row.GetValue<DateTimeOffset>("updated_at").UtcDateTime,
                visibility,
                new(row.GetValue<string>("user_id")));
        }, _options, cancellationToken);
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
                Visibility = row.GetValue<short?>("security_level").FromCode(VisibilityLevel.Public)
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
