using System.Collections.Concurrent;
using Cassandra;
using Interfold.Contracts.Configuration;
using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Models.Read;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Interfold.Infrastructure.Scylla.Repository;

public sealed class ScyllaAlterRepository : IAlterRepository
{
    private readonly IScyllaSessionProvider _sessionProvider;
    private readonly IScyllaKeyspaceResolver _keyspaceResolver;
    private readonly ISettingsFieldRepository _settingsFields;
    private readonly IPollRepository _pollRepository;
    private readonly PersistenceConfiguration _options;
    private readonly ILogger<ScyllaAlterRepository> _logger;
    private static readonly ConcurrentDictionary<(int ClusterId, string Keyspace), byte> UdtMappings = new();

    public ScyllaAlterRepository(
        IScyllaSessionProvider sessionProvider,
        IScyllaKeyspaceResolver keyspaceResolver,
        ISettingsFieldRepository settingsFields,
        IPollRepository pollRepository,
        IOptions<PersistenceConfiguration> options,
        ILogger<ScyllaAlterRepository> logger
    )
    {
        _sessionProvider = sessionProvider;
        _keyspaceResolver = keyspaceResolver;
        _settingsFields = settingsFields;
        _pollRepository = pollRepository;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<AlterId?> CreateAsync(SystemId systemId, CreateAlterCommand command, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync<AlterId?>(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            var nextIdQuery = new SimpleStatement(
                $"SELECT id FROM {keyspace}.alters WHERE user_id = ? ORDER BY id DESC LIMIT 1",
                normalizedSystemId
            );

            var rows = await session.ExecuteAsync(nextIdQuery);
            var current = rows.FirstOrDefault()?.GetValue<short>("id") ?? (short)0;
            var next = (short)(current + 1);
            var createdAt = command.CreatedAt.ToUniversalTime();

            var insert = new SimpleStatement(
                $"INSERT INTO {keyspace}.alters (user_id, id, name, alias, inserted_at, updated_at) VALUES (?, ?, ?, ?, ?, ?)",
                normalizedSystemId,
                next,
                command.Name,
                null,
                createdAt,
                createdAt
            );

            await session.ExecuteAsync(insert);
            return new(next);
        }, _options, cancellationToken, _logger);
    }

    public async Task<bool> ExistsAsync(SystemId systemId, AlterId alterId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            var query = new SimpleStatement(
                $"SELECT id FROM {keyspace}.alters WHERE user_id = ? AND id = ? LIMIT 1",
                normalizedSystemId,
                alterId.Value
            );

            var rows = await session.ExecuteAsync(query);
            return rows.Any();
        }, _options, cancellationToken, _logger);
    }

    public async Task<bool> UpdateAsync(SystemId systemId, UpdateAlterCommand command, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);
            var updatedAt = command.UpdatedAt.ToUniversalTime();

            var batch = new BatchStatement();

            var exists = await ExistsAsync(session, keyspace, normalizedSystemId, command.AlterId);
            if (!exists)
            {
                return false;
            }

            UpdateIfNotNull(batch, keyspace, command, "name", command.Name, normalizedSystemId, updatedAt);
            UpdateIfNotNull(batch, keyspace, command, "description", command.Description, normalizedSystemId, updatedAt);
            if (command.ClearAvatar)
            {
                batch.Add(new SimpleStatement(
                    $"UPDATE {keyspace}.alters SET avatar_url = ?, avatar_source = ?, updated_at = ? WHERE user_id = ? AND id = ?",
                    null,
                    null,
                    updatedAt,
                    normalizedSystemId,
                    command.AlterId.Value
                ));
            }
            else if (command.AvatarUrl is { } avatarUrl)
            {
                // avatar_url + avatar_source must move together; the domain handler
                // rejects the half-set case so we can write both unconditionally here.
                var sourceShort = (short)(command.AvatarSource ?? AvatarSource.Local);
                batch.Add(new SimpleStatement(
                    $"UPDATE {keyspace}.alters SET avatar_url = ?, avatar_source = ?, updated_at = ? WHERE user_id = ? AND id = ?",
                    avatarUrl.Value,
                    sourceShort,
                    updatedAt,
                    normalizedSystemId,
                    command.AlterId.Value
                ));
            }
            UpdateIfNotNull(batch, keyspace, command, "color", command.Color?.Value, normalizedSystemId, updatedAt);
            UpdateIfNotNull(batch, keyspace, command, "pronouns", command.Pronouns, normalizedSystemId, updatedAt);
            UpdateIfNotNull(batch, keyspace, command, "security_level", (short?)command.SecurityLevel, normalizedSystemId, updatedAt);

            if (command.Fields is not null)
            {
                EnsureAlterFieldUdtMapping(session, keyspace);

                var currentRow = (await session.ExecuteAsync(new SimpleStatement(
                    $"SELECT fields FROM {keyspace}.alters WHERE user_id = ? AND id = ? LIMIT 1",
                    normalizedSystemId,
                    command.AlterId.Value
                ))).FirstOrDefault();

                var merged = (currentRow?.GetValue<IEnumerable<AlterFieldUdt>?>("fields") ?? [])
                    .ToDictionary(x => x.Id, x => x.Value);

                foreach (var f in command.Fields)
                {
                    merged[f.Id] = f.Value;
                }

                var udts = merged
                    .Select(kvp => new AlterFieldUdt { Id = kvp.Key, Value = kvp.Value })
                    .ToList();

                batch.Add(new SimpleStatement(
                    $"UPDATE {keyspace}.alters SET fields = ?, updated_at = ? WHERE user_id = ? AND id = ?",
                    udts,
                    updatedAt,
                    normalizedSystemId,
                    command.AlterId.Value
                ));
            }

            UpdateIfNotNull(batch, keyspace, command, "proxy_name", command.ProxyName, normalizedSystemId, updatedAt);
            UpdateIfNotNull(batch, keyspace, command, "untracked", command.Untracked, normalizedSystemId, updatedAt);
            UpdateIfNotNull(batch, keyspace, command, "archived", command.Archived, normalizedSystemId, updatedAt);
            UpdateIfNotNull(batch, keyspace, command, "pinned", command.Pinned, normalizedSystemId, updatedAt);

            // Handle alias changes with lookup table maintenance
            if (command.Alias is not null)
            {
                // Read old alias to remove from lookup table
                var oldAliasRow = (await session.ExecuteAsync(new SimpleStatement(
                    $"SELECT alias FROM {keyspace}.alters WHERE user_id = ? AND id = ? LIMIT 1",
                    normalizedSystemId, command.AlterId.Value))).FirstOrDefault();
                var oldAlias = oldAliasRow?.GetValue<string?>("alias");

                // Update the base table
                batch.Add(new SimpleStatement(
                    $"UPDATE {keyspace}.alters SET alias = ?, updated_at = ? WHERE user_id = ? AND id = ?",
                    command.Alias, updatedAt, normalizedSystemId, command.AlterId.Value));

                // Remove old lookup entry
                if (!string.IsNullOrWhiteSpace(oldAlias))
                {
                    batch.Add(new SimpleStatement(
                        $"DELETE FROM {keyspace}.alters_by_alias WHERE user_id = ? AND alias = ?",
                        normalizedSystemId, oldAlias));
                }

                // Insert new lookup entry (if not clearing alias)
                var newAlias = command.Alias as string;
                if (!string.IsNullOrWhiteSpace(newAlias))
                {
                    batch.Add(new SimpleStatement(
                        $"INSERT INTO {keyspace}.alters_by_alias (user_id, alias, alter_id) VALUES (?, ?, ?)",
                        normalizedSystemId, newAlias, command.AlterId.Value));
                }
            }

            await session.ExecuteAsync(batch);

            return true;
        }, _options, cancellationToken, _logger);
    }

    private void UpdateIfNotNull(BatchStatement batch, string keyspace, UpdateAlterCommand command, string field, object? value, string normalizedSystemId, DateTimeOffset updatedAt)
    {
        if (value is not null)
        {
            batch.Add(new SimpleStatement(
                    $"UPDATE {keyspace}.alters SET {field} = ?, updated_at = ? WHERE user_id = ? AND id = ?",
                    value,
                    updatedAt,
                    normalizedSystemId,
                    command.AlterId.Value
                ));
        }
    }

    public async Task<bool> DeleteAsync(SystemId systemId, AlterId alterId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);
            var alterIdShort = alterId.Value;

            var exists = await ExistsAsync(session, keyspace, normalizedSystemId, alterId);
            if (!exists)
            {
                return false;
            }

            var deleteBatch = new BatchStatement();
            deleteBatch.Add(new SimpleStatement(
                $"DELETE FROM {keyspace}.alters WHERE user_id = ? AND id = ?",
                normalizedSystemId,
                alterIdShort));
            deleteBatch.Add(new SimpleStatement(
                $"DELETE FROM {keyspace}.current_fronts WHERE user_id = ? AND alter_id = ?",
                normalizedSystemId,
                alterIdShort));
            await session.ExecuteAsync(deleteBatch);

            // Parallelize the three cascade queries
            var frontsTask = session.ExecuteAsync(new SimpleStatement(
                $"SELECT id, time_start FROM {keyspace}.fronts_by_alter WHERE user_id = ? AND alter_id = ?",
                normalizedSystemId,
                alterIdShort));

            var journalEntriesTask = session.ExecuteAsync(new SimpleStatement(
                $"SELECT id FROM {keyspace}.alter_journals_by_alter WHERE user_id = ? AND alter_id = ?",
                normalizedSystemId,
                alterIdShort));

            var primaryTask = session.ExecuteAsync(new SimpleStatement(
                $"SELECT primary_front_alter FROM {keyspace}.users WHERE id = ? LIMIT 1",
                normalizedSystemId));

            var tagsTask = session.ExecuteAsync(new SimpleStatement(
                $"SELECT tag_id FROM {keyspace}.alter_tags_by_alter WHERE user_id = ? AND alter_id = ?",
                normalizedSystemId,
                alterIdShort));

            var globalJournalAltersTask = session.ExecuteAsync(new SimpleStatement(
                $"SELECT global_journal_id FROM {keyspace}.global_journal_alters WHERE user_id = ? AND alter_id = ? ALLOW FILTERING",
                normalizedSystemId,
                alterIdShort));

            var aliasTask = session.ExecuteAsync(new SimpleStatement(
                $"SELECT alias FROM {keyspace}.alters WHERE user_id = ? AND id = ? LIMIT 1",
                normalizedSystemId,
                alterIdShort));

            var removePollsTask = _pollRepository.RemoveAlterFromPollsAsync(systemId, alterId, cancellationToken);

            await Task.WhenAll(
                frontsTask,
                journalEntriesTask,
                primaryTask,
                tagsTask,
                globalJournalAltersTask,
                removePollsTask,
                aliasTask
            );

            var frontRows = await frontsTask;
            var journalEntryRows = await journalEntriesTask;
            var primaryFrontRow = (await primaryTask).FirstOrDefault();
            var membershipRows = await tagsTask;
            var globalJournalAlterRows = await globalJournalAltersTask;
            var aliasRow = (await aliasTask).FirstOrDefault();
            var alias = aliasRow?.GetValue<string?>("alias");

            // Batch all front deletes (base table + denormalized fronts_by_alter)
            if (frontRows.Any())
            {
                var frontBatch = new BatchStatement();
                foreach (var frontRow in frontRows)
                {
                    var frontId = frontRow.GetValue<Guid>("id");
                    var timeStart = frontRow.GetValue<DateTimeOffset>("time_start");
                    frontBatch.Add(new SimpleStatement(
                        $"DELETE FROM {keyspace}.fronts WHERE user_id = ? AND id = ? AND time_start = ?",
                        normalizedSystemId, frontId, timeStart));
                    frontBatch.Add(new SimpleStatement(
                        $"DELETE FROM {keyspace}.fronts_by_alter WHERE user_id = ? AND alter_id = ? AND id = ? AND time_start = ?",
                        normalizedSystemId, alterIdShort, frontId, timeStart));
                    // fronts_by_time and fronts_by_end_time entries are only present for closed fronts;
                    // delete unconditionally (no-op if not present)
                    frontBatch.Add(new SimpleStatement(
                        $"DELETE FROM {keyspace}.fronts_by_time WHERE user_id = ? AND time_start = ? AND time_end = ? AND id = ?",
                        normalizedSystemId, timeStart, DateTimeOffset.MaxValue, frontId));
                    frontBatch.Add(new SimpleStatement(
                        $"DELETE FROM {keyspace}.fronts_by_end_time WHERE user_id = ? AND time_end = ? AND time_start = ? AND id = ?",
                        normalizedSystemId, DateTimeOffset.MaxValue, timeStart, frontId));
                }
                await session.ExecuteAsync(frontBatch);
            }

            // Batch all journal entry deletes (base table + denormalized)
            if (journalEntryRows.Any())
            {
                var journalBatch = new BatchStatement();
                foreach (var row in journalEntryRows)
                {
                    var journalId = row.GetValue<Guid>("id");
                    journalBatch.Add(new SimpleStatement(
                        $"DELETE FROM {keyspace}.alter_journals WHERE user_id = ? AND id = ? AND alter_id = ?",
                        normalizedSystemId, journalId, alterIdShort));
                    journalBatch.Add(new SimpleStatement(
                        $"DELETE FROM {keyspace}.alter_journals_by_alter WHERE user_id = ? AND alter_id = ? AND id = ?",
                        normalizedSystemId, alterIdShort, journalId));
                }
                await session.ExecuteAsync(journalBatch);
            }

            // If this alter is currently the primary front, clear it.
            var currentPrimary = primaryFrontRow?.GetValue<short?>("primary_front_alter") is { } primaryShort
                ? new AlterId(primaryShort)
                : (AlterId?)null;
            if (currentPrimary == new AlterId(alterIdShort))
            {
                await session.ExecuteAsync(new SimpleStatement(
                    $"UPDATE {keyspace}.users SET primary_front_alter = null WHERE id = ?",
                    normalizedSystemId));
            }

            // Batch all tag deletes (base table + denormalized)
            if (membershipRows.Any())
            {
                var tagBatch = new BatchStatement();
                foreach (var row in membershipRows)
                {
                    var tagId = row.GetValue<Guid>("tag_id");
                    tagBatch.Add(new SimpleStatement(
                        $"DELETE FROM {keyspace}.alter_tags WHERE user_id = ? AND tag_id = ? AND alter_id = ?",
                        normalizedSystemId, tagId, alterIdShort));
                }
                // Delete all entries in alter_tags_by_alter for this alter (single partition delete)
                tagBatch.Add(new SimpleStatement(
                    $"DELETE FROM {keyspace}.alter_tags_by_alter WHERE user_id = ? AND alter_id = ?",
                    normalizedSystemId, alterIdShort));
                await session.ExecuteAsync(tagBatch);
            }

            // Batch all global journal alter deletes
            if (globalJournalAlterRows.Any())
            {
                var gjaBatch = new BatchStatement();
                foreach (var row in globalJournalAlterRows)
                {
                    gjaBatch.Add(new SimpleStatement(
                        $"DELETE FROM {keyspace}.global_journal_alters WHERE user_id = ? AND global_journal_id = ? AND alter_id = ?",
                        normalizedSystemId,
                        row.GetValue<Guid>("global_journal_id"),
                        alterIdShort));
                }
                await session.ExecuteAsync(gjaBatch);
            }

            // Clean up alters_by_alias if alter had an alias
            if (!string.IsNullOrWhiteSpace(alias))
            {
                await session.ExecuteAsync(new SimpleStatement(
                    $"DELETE FROM {keyspace}.alters_by_alias WHERE user_id = ? AND alias = ?",
                    normalizedSystemId, alias));
            }

            return true;
        }, _options, cancellationToken, _logger);
    }

    public async Task<IReadOnlyList<AlterReadModel>> ListAsync(SystemId systemId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            var definitions = await _settingsFields.ListAsync(systemId, cancellationToken);
            EnsureAlterFieldUdtMapping(session, keyspace);

            var query = new SimpleStatement(
                $"SELECT id, name, alias, fields, security_level, color, pronouns, avatar_url, avatar_source, pinned, archived, untracked, description, proxy_name FROM {keyspace}.alters WHERE user_id = ?",
                normalizedSystemId
            );

            var rows = await session.ExecuteAsync(query);
            return rows
                .Select(row => new AlterReadModel(
                    new(row.GetValue<short>("id")),
                    row.GetValue<string>("name"),
                    row.GetValue<string?>("description"),
                    AvatarUrl.FromNullable(row.GetValue<string?>("avatar_url")),
                    row.GetValue<short?>("avatar_source").TryFromCode<AvatarSource>(out var src) ? src : null,
                    HexColor.FromNullable(row.GetValue<string?>("color")),
                    row.GetValue<string?>("pronouns"),
                    row.GetValue<short?>("security_level").FromCode(VisibilityLevel.Public),
                    ResolveFields(row.GetValue<IEnumerable<AlterFieldUdt>?>("fields"), definitions),
                    row.GetValue<string?>("proxy_name"),
                    row.GetValue<string?>("alias"),
                    row.GetValue<bool?>("untracked"),
                    row.GetValue<bool?>("archived"),
                    row.GetValue<bool?>("pinned")
                ))
                .OrderBy(x => x.Id.Value)
                .ToArray();
        }, _options, cancellationToken, _logger);
    }

    public async Task<IReadOnlyList<BareAlter>> ListGuardedAsync(
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
            EnsureAlterFieldUdtMapping(session, keyspace);
            var definitions = await ResolveVisibleDefinitionsAsync(systemId, friendshipLevel, cancellationToken);

            var query = new SimpleStatement(
                $"SELECT id, name, avatar_url, avatar_source, color, description, pronouns, pinned, security_level, fields FROM {keyspace}.alters WHERE user_id = ?",
                normalizedSystemId
            );

            var rows = await session.ExecuteAsync(query);
            return rows
                .Where(row => row.GetValue<short?>("security_level").FromCode(VisibilityLevel.Public).CanBeViewedBy(friendshipLevel))
                .Select(row => new BareAlter(
                    new(row.GetValue<short>("id")),
                    row.GetValue<string>("name"),
                    AvatarUrl.FromNullable(row.GetValue<string?>("avatar_url")),
                    row.GetValue<short?>("avatar_source").TryFromCode<AvatarSource>(out var src) ? src : null,
                    HexColor.FromNullable(row.GetValue<string?>("color")),
                    row.GetValue<string?>("pronouns"),
                    row.GetValue<string?>("description"),
                    ResolveGuardedFields(row.GetValue<IEnumerable<AlterFieldUdt>?>("fields"), definitions)))
                .OrderBy(x => x.Id.Value)
                .ToArray();
        }, _options, cancellationToken, _logger);
    }

    public async Task<AlterReadModel?> GetAsync(SystemId systemId, AlterId alterId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            var definitions = await _settingsFields.ListAsync(systemId, cancellationToken);
            EnsureAlterFieldUdtMapping(session, keyspace);

            var query = new SimpleStatement(
                $"SELECT id, name, alias, fields, security_level, color, pronouns, avatar_url, avatar_source, pinned, archived, untracked, description, proxy_name FROM {keyspace}.alters WHERE user_id = ? AND id = ? LIMIT 1",
                normalizedSystemId,
                alterId.Value
            );

            var row = (await session.ExecuteAsync(query)).FirstOrDefault();
            return row is null
                ? null
                : new AlterReadModel(
                    new(row.GetValue<short>("id")),
                    row.GetValue<string>("name"),
                    row.GetValue<string?>("description"),
                    AvatarUrl.FromNullable(row.GetValue<string?>("avatar_url")),
                    row.GetValue<short?>("avatar_source").TryFromCode<AvatarSource>(out var src) ? src : null,
                    HexColor.FromNullable(row.GetValue<string?>("color")),
                    row.GetValue<string?>("pronouns"),
                    row.GetValue<short?>("security_level").FromCode(VisibilityLevel.Public),
                    ResolveFields(row.GetValue<IEnumerable<AlterFieldUdt>?>("fields"), definitions),
                    row.GetValue<string?>("proxy_name"),
                    row.GetValue<string?>("alias"),
                    row.GetValue<bool?>("untracked"),
                    row.GetValue<bool?>("archived"),
                    row.GetValue<bool?>("pinned")
                );
        }, _options, cancellationToken, _logger);
    }

    public async Task<BareAlter?> GetGuardedAsync(
        SystemId systemId,
        AlterId alterId,
        SystemId? viewerSystemId,
        CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);
            var friendshipLevel = await ScyllaSharedQueries.ResolveFriendshipLevelAsync(session, _keyspaceResolver, new(normalizedSystemId), viewerSystemId);
            EnsureAlterFieldUdtMapping(session, keyspace);
            var definitions = await ResolveVisibleDefinitionsAsync(systemId, friendshipLevel, cancellationToken);

            var query = new SimpleStatement(
                $"SELECT id, name, avatar_url, avatar_source, description, color, pronouns, pinned, security_level, fields FROM {keyspace}.alters WHERE user_id = ? AND id = ? LIMIT 1",
                normalizedSystemId,
                alterId.Value
            );

            var row = (await session.ExecuteAsync(query)).FirstOrDefault();
            if (row is null)
            {
                return null;
            }

            var securityLevel = row.GetValue<short?>("security_level").FromCode(VisibilityLevel.Public);
            if (!securityLevel.CanBeViewedBy(friendshipLevel))
            {
                return null;
            }

            return new BareAlter(
                new(row.GetValue<short>("id")),
                row.GetValue<string>("name"),
                AvatarUrl.FromNullable(row.GetValue<string?>("avatar_url")),
                row.GetValue<short?>("avatar_source").TryFromCode<AvatarSource>(out var src) ? src : null,
                HexColor.FromNullable(row.GetValue<string?>("color")),
                row.GetValue<string?>("pronouns"),
                row.GetValue<string?>("description"),
                ResolveGuardedFields(row.GetValue<IEnumerable<AlterFieldUdt>?>("fields"), definitions)
            );
        }, _options, cancellationToken, _logger);
    }

    public async Task<bool> AliasTakenByOtherAsync(
        SystemId systemId,
        AlterId alterId,
        string alias,
        CancellationToken cancellationToken = default
    )
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            var query = new SimpleStatement(
                $"SELECT id, alias FROM {keyspace}.alters WHERE user_id = ? AND alias = ?",
                normalizedSystemId,
                alias
            );

            var rows = await session.ExecuteAsync(query);
            return rows.Any(row => row.GetValue<short>("id") != alterId.Value);
        }, _options, cancellationToken, _logger);
    }

    private static async Task<bool> ExistsAsync(ISession session, string keyspace, string normalizedSystemId, AlterId alterId)
    {
        var query = new SimpleStatement(
            $"SELECT id FROM {keyspace}.alters WHERE user_id = ? AND id = ? LIMIT 1",
            normalizedSystemId,
            alterId.Value
        );

        var rows = await session.ExecuteAsync(query);
        return rows.Any();
    }

    private async Task<IReadOnlyList<SettingsFieldReadModel>> ResolveVisibleDefinitionsAsync(
        SystemId systemId,
        FriendshipLevel? friendshipLevel,
        CancellationToken cancellationToken)
    {
        var definitions = await _settingsFields.ListAsync(systemId, cancellationToken);
        return definitions
            .Where(def => def.SecurityLevel.CanBeViewedBy(friendshipLevel))
            .ToArray();
    }

    private static IReadOnlyList<AlterPublicFieldReadModel> ResolveFields(
        IEnumerable<AlterFieldUdt>? alterFields,
        IReadOnlyList<SettingsFieldReadModel> definitions)
        => ScyllaSharedQueries.ResolveAlterFields(alterFields, definitions);

    private static IReadOnlyList<AlterPublicFieldReadModel> ResolveGuardedFields(
        IEnumerable<AlterFieldUdt>? alterFields,
        IReadOnlyList<SettingsFieldReadModel> definitions)
    {
        var valuesByFieldId = (alterFields ?? Array.Empty<AlterFieldUdt>())
            .ToDictionary(x => x.Id, x => x.Value);

        if (valuesByFieldId.Count == 0 || definitions.Count == 0)
        {
            return [];
        }

        return definitions
            .Where(def => valuesByFieldId.ContainsKey(def.Id))
            .Select(def => new AlterPublicFieldReadModel(def.Id, def.Name, def.Type, valuesByFieldId[def.Id]))
            .ToArray();
    }

    public static void EnsureAlterFieldUdtMapping(ISession session, string keyspace)
    {
        var key = (System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(session.Cluster), keyspace);
        if (UdtMappings.ContainsKey(key))
        {
            return;
        }

        session.UserDefinedTypes.Define(
            UdtMap.For<AlterFieldUdt>("alter_field", keyspace)
                .Map(f => f.Id, "id")
                .Map(f => f.Value, "value"));

        UdtMappings.TryAdd(key, 0);
    }

    public sealed class AlterFieldUdt
    {
        public Guid Id { get; set; }
        public string? Value { get; set; }
    }
}
