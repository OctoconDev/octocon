using Cassandra;
using Interfold.Contracts.Configuration;
using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Read;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using static Interfold.Infrastructure.Scylla.Repository.ScyllaAlterRepository;

namespace Interfold.Infrastructure.Scylla.Repository;

public sealed class ScyllaFrontingRepository : IFrontingRepository
{
    private sealed record CurrentFrontRow(Guid FrontId, short AlterId, DateTimeOffset StartedAt, string? Comment);

    private readonly IScyllaSessionProvider _sessionProvider;
    private readonly IScyllaKeyspaceResolver _keyspaceResolver;
    private readonly PersistenceConfiguration _options;
    private readonly ILogger<ScyllaFrontingRepository> _logger;
    private readonly ISettingsFieldRepository _settingsFields;

    public ScyllaFrontingRepository(
        IScyllaSessionProvider sessionProvider,
        IScyllaKeyspaceResolver keyspaceResolver,
        IOptions<PersistenceConfiguration> options,
        ILogger<ScyllaFrontingRepository> logger,
        ISettingsFieldRepository settingsFields
    )
    {
        _sessionProvider = sessionProvider;
        _keyspaceResolver = keyspaceResolver;
        _options = options.Value;
        _logger = logger;
        _settingsFields = settingsFields;
    }

    public async Task<bool> IsFrontingAsync(SystemId systemId, AlterId alterId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            var query = new SimpleStatement(
                $"SELECT alter_id FROM {keyspace}.current_fronts WHERE user_id = ? AND alter_id = ? LIMIT 1",
                normalizedSystemId,
                alterId.Value
            );

            var rows = await session.ExecuteAsync(query);
            return rows.Any();
        }, _options, cancellationToken, _logger);
    }

    public async Task<FrontId?> StartAsync(
        SystemId systemId,
        AlterId alterId,
        string? comment,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken = default
    )
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync<FrontId?>(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);
            var frontGuid = Guid.NewGuid();

            var startBatch = new BatchStatement();
            startBatch.Add(new SimpleStatement(
                $"INSERT INTO {keyspace}.current_fronts (user_id, alter_id, id, comment, time_start, inserted_at, updated_at) VALUES (?, ?, ?, ?, ?, ?, ?)",
                normalizedSystemId,
                alterId.Value,
                frontGuid,
                comment,
                startedAt,
                startedAt,
                startedAt
            ));
            startBatch.Add(new SimpleStatement(
                $"INSERT INTO {keyspace}.fronts (user_id, id, alter_id, comment, time_start, inserted_at, updated_at) VALUES (?, ?, ?, ?, ?, ?, ?)",
                normalizedSystemId,
                frontGuid,
                alterId.Value,
                comment,
                startedAt,
                startedAt,
                startedAt
            ));
            // Maintain fronts_by_alter denormalized table
            startBatch.Add(new SimpleStatement(
                $"INSERT INTO {keyspace}.fronts_by_alter (user_id, alter_id, id, comment, time_start, inserted_at, updated_at) VALUES (?, ?, ?, ?, ?, ?, ?)",
                normalizedSystemId,
                alterId.Value,
                frontGuid,
                comment,
                startedAt,
                startedAt,
                startedAt
            ));
            // Note: fronts_by_time and fronts_by_end_time are only populated when a front is closed
            await session.ExecuteAsync(startBatch);
            return new(frontGuid);
        }, _options, cancellationToken, _logger);
    }

    public async Task<bool> EndAsync(SystemId systemId, AlterId alterId, DateTimeOffset endedAt, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            var current = await GetCurrentFrontRowAsync(session, keyspace, normalizedSystemId, alterId);
            if (current is null)
            {
                return false;
            }

            // Prefetch primary_front_alter so we know whether ending this front should
            // also clear the primary.
            var primaryRow = (await session.ExecuteAsync(new SimpleStatement(
                $"SELECT primary_front_alter FROM {keyspace}.users WHERE id = ? LIMIT 1",
                normalizedSystemId))).FirstOrDefault();
            var primaryAlterId = primaryRow?.GetValue<short?>("primary_front_alter") is { } primaryShort
                ? new AlterId(primaryShort)
                : (AlterId?)null;

            var endBatch = new BatchStatement();
            endBatch.Add(new SimpleStatement(
                $"UPDATE {keyspace}.fronts SET time_end = ?, updated_at = ? WHERE user_id = ? AND id = ? AND time_start = ?",
                endedAt,
                endedAt,
                normalizedSystemId,
                current.FrontId,
                current.StartedAt));
            endBatch.Add(new SimpleStatement(
                $"DELETE FROM {keyspace}.current_fronts WHERE user_id = ? AND alter_id = ?",
                normalizedSystemId,
                alterId.Value));
            // Maintain fronts_by_alter (update time_end)
            endBatch.Add(new SimpleStatement(
                $"UPDATE {keyspace}.fronts_by_alter SET time_end = ?, updated_at = ? WHERE user_id = ? AND alter_id = ? AND id = ? AND time_start = ?",
                endedAt, endedAt, normalizedSystemId, alterId.Value, current.FrontId, current.StartedAt));
            // Insert into fronts_by_time (only on close)
            endBatch.Add(new SimpleStatement(
                $"INSERT INTO {keyspace}.fronts_by_time (user_id, time_start, time_end, id, alter_id, comment, inserted_at, updated_at) VALUES (?, ?, ?, ?, ?, ?, ?, ?)",
                normalizedSystemId, current.StartedAt, endedAt, current.FrontId, alterId.Value, current.Comment, endedAt, endedAt));
            // Insert into fronts_by_end_time (only on close)
            endBatch.Add(new SimpleStatement(
                $"INSERT INTO {keyspace}.fronts_by_end_time (user_id, time_end, time_start, id, alter_id, comment, inserted_at, updated_at) VALUES (?, ?, ?, ?, ?, ?, ?, ?)",
                normalizedSystemId, endedAt, current.StartedAt, current.FrontId, alterId.Value, current.Comment, endedAt, endedAt));

            if (primaryAlterId == alterId)
            {
                endBatch.Add(new SimpleStatement(
                    $"UPDATE {keyspace}.users SET primary_front_alter = null, updated_at = toTimestamp(now()) WHERE id = ?",
                    normalizedSystemId));
            }
            
            await session.ExecuteAsync(endBatch);

            return true;
        }, _options, cancellationToken, _logger);
    }

    public async Task<bool> SetPrimaryAsync(SystemId systemId, AlterId? alterId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            if (alterId is { } id)
            {
                var frontingRow = (await session.ExecuteAsync(new SimpleStatement(
                    $"SELECT alter_id FROM {keyspace}.current_fronts WHERE user_id = ? AND alter_id = ? LIMIT 1",
                    normalizedSystemId,
                    id.Value))).FirstOrDefault();
                if (frontingRow is null)
                {
                    return false;
                }

                await session.ExecuteAsync(new SimpleStatement(
                    $"UPDATE {keyspace}.users SET primary_front_alter = ?, updated_at = toTimestamp(now()) WHERE id = ?",
                    id.Value,
                    normalizedSystemId));

                return true;
            }

            await session.ExecuteAsync(new SimpleStatement(
                $"UPDATE {keyspace}.users SET primary_front_alter = null, updated_at = toTimestamp(now()) WHERE id = ?",
                normalizedSystemId));

            return true;
        }, _options, cancellationToken, _logger);
    }

    public async Task<IReadOnlyList<FrontActiveReadModel>> ListActiveAsync(SystemId systemId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            // Register UDT mapping before selecting the fields UDT column.
            EnsureAlterFieldUdtMapping(session, keyspace);

            var primaryTask = session.ExecuteAsync(new SimpleStatement(
                $"SELECT primary_front_alter FROM {keyspace}.users WHERE id = ? LIMIT 1",
                normalizedSystemId
            ));

            var activeTask = session.ExecuteAsync(new SimpleStatement(
                $"SELECT alter_id, id, comment, time_start FROM {keyspace}.current_fronts WHERE user_id = ?",
                normalizedSystemId
            ));

            var altersTask = session.ExecuteAsync(new SimpleStatement(
                $"SELECT id, name, avatar_url, avatar_source, description, color, fields, pronouns, pinned FROM {keyspace}.alters WHERE user_id = ?",
                normalizedSystemId));

            await Task.WhenAll(primaryTask, activeTask, altersTask);

            var primaryRow = (await primaryTask).FirstOrDefault();
            var primaryAlterId = primaryRow?.GetValue<short?>("primary_front_alter") is { } primaryShort
                ? new AlterId(primaryShort)
                : (AlterId?)null;
            
            var rows = await activeTask;
            var alterRows = await altersTask;

            var definitions = await _settingsFields.ListAsync(systemId, cancellationToken);

            var alterById = alterRows.ToDictionary(
                row => new AlterId(row.GetValue<short>("id")),
                row => new BareAlter(
                    new(row.GetValue<short>("id")),
                    row.GetValue<string>("name"),
                    AvatarUrl.FromNullable(row.GetValue<string?>("avatar_url")),
                    row.GetValue<short?>("avatar_source").TryFromCode<AvatarSource>(out var src) ? src : null,
                    HexColor.FromNullable(row.GetValue<string?>("color")),
                    row.GetValue<string?>("pronouns"),
                    row.GetValue<string?>("description"),
                    ResolveFields(row.GetValue<IEnumerable<AlterFieldUdt>?>("fields"), definitions)));

            return (IReadOnlyList<FrontActiveReadModel>)rows
                .Select(row =>
                {
                    FrontId frontId = new(row.GetValue<Guid>("id"));
                    AlterId alterId = new(row.GetValue<short>("alter_id"));
                    var timeStart = row.GetValue<DateTimeOffset?>("time_start") ?? DateTimeOffset.UtcNow;
                    var front = new FrontHistoryReadModel(
                        frontId,
                        alterId,
                        row.GetValue<string?>("comment"),
                        timeStart,
                        null,
                        new(normalizedSystemId));

                    if (!alterById.TryGetValue(alterId, out var alter))
                    {
                        alter = new BareAlter(alterId, $"Alter {alterId}", null, null, null, null, null, null!);
                    }

                    return new FrontActiveReadModel(alter, front, primaryAlterId == alterId);
                })
                .OrderByDescending(x => x.Front.TimeStart)
                .ToArray();
        }, _options, cancellationToken, _logger);
    }

    private static IReadOnlyList<AlterPublicFieldReadModel> ResolveFields(
        IEnumerable<AlterFieldUdt>? alterFields,
        IReadOnlyList<SettingsFieldReadModel> definitions)
        => ScyllaSharedQueries.ResolveAlterFields(alterFields, definitions);

    public async Task<IReadOnlyList<FrontActiveReadModel>> ListActiveGuardedAsync(
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

            var all = await ListActiveAsync(systemId, cancellationToken);
            if (all.Count == 0)
            {
                return all;
            }

            var securityRows = await session.ExecuteAsync(new SimpleStatement(
                $"SELECT id, security_level FROM {keyspace}.alters WHERE user_id = ?",
                normalizedSystemId));

            var visibilityByAlterId = securityRows.ToDictionary(
                row => new AlterId(row.GetValue<short>("id")),
                row => row.GetValue<short?>("security_level").FromCode(VisibilityLevel.Public));

            var filtered = all
                .Where(front => visibilityByAlterId.TryGetValue(front.Alter.Id, out var level) && level.CanBeViewedBy(friendshipLevel))
                .ToArray();

            return (IReadOnlyList<FrontActiveReadModel>)filtered;
        }, _options, cancellationToken, _logger);
    }

    public async Task<IReadOnlyList<FrontHistoryReadModel>> ListHistoryBetweenAsync(
        SystemId systemId,
        DateTimeOffset startInclusive,
        DateTimeOffset endInclusive,
        CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            var historyQuery = new SimpleStatement(
                $"SELECT id, alter_id, comment, time_start, time_end FROM {keyspace}.fronts_by_time WHERE user_id = ? AND time_start >= ? AND time_start <= ?",
                normalizedSystemId,
                startInclusive,
                endInclusive
            );

            var historicalRows = await session.ExecuteAsync(historyQuery);
            var historical = historicalRows
                .Select(row => new FrontHistoryReadModel(
                    new(row.GetValue<Guid>("id")),
                    new(row.GetValue<short>("alter_id")),
                    row.GetValue<string?>("comment"),
                    row.GetValue<DateTimeOffset>("time_start"),
                    row.GetValue<DateTimeOffset?>("time_end"),
                    new(normalizedSystemId)))
                .Where(x => x.TimeEnd != null)
                .ToList();

            return (IReadOnlyList<FrontHistoryReadModel>)historical
                .DistinctBy(x => x.Id)
                .OrderByDescending(x => x.TimeStart)
                .ToArray();
        }, _options, cancellationToken, _logger);
    }

    public async Task<FrontActiveReadModel?> GetActiveByFrontIdAsync(SystemId systemId, FrontId frontId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            // Identify the alter for this specific front record
            var frontRow = (await session.ExecuteAsync(new SimpleStatement(
                $"SELECT alter_id FROM {keyspace}.fronts WHERE user_id = ? AND id = ? LIMIT 1 ALLOW FILTERING",
                normalizedSystemId,
                frontId.Value))).FirstOrDefault();

            if (frontRow is null)
                return null;

            var alterId = frontRow.GetValue<short>("alter_id");

            EnsureAlterFieldUdtMapping(session, keyspace);
            var definitions = await _settingsFields.ListAsync(systemId, cancellationToken);

            // Parallelize the three remaining targeted lookups
            var currentTask = session.ExecuteAsync(new SimpleStatement(
                $"SELECT id, alter_id, comment, time_start FROM {keyspace}.current_fronts WHERE user_id = ? AND alter_id = ? LIMIT 1",
                normalizedSystemId, alterId));

            var alterTask = session.ExecuteAsync(new SimpleStatement(
                $"SELECT id, name, avatar_url, avatar_source, description, color, fields, pronouns, pinned FROM {keyspace}.alters WHERE user_id = ? AND id = ? LIMIT 1",
                normalizedSystemId, alterId));

            var primaryTask = session.ExecuteAsync(new SimpleStatement(
                $"SELECT primary_front_alter FROM {keyspace}.users WHERE id = ? LIMIT 1",
                normalizedSystemId));

            await Task.WhenAll(currentTask, alterTask, primaryTask);

            var currentRow = (await currentTask).FirstOrDefault();
            if (currentRow is null || currentRow.GetValue<Guid>("id") != frontId.Value)
                return null;

            var alterRow = (await alterTask).FirstOrDefault();
            var primaryRow = (await primaryTask).FirstOrDefault();
            var primaryAlterId = primaryRow?.GetValue<short?>("primary_front_alter") is { } primaryShort
                ? new AlterId(primaryShort)
                : (AlterId?)null;

            BareAlter alter;
            if (alterRow is null)
            {
                alter = new BareAlter(new(alterId), $"Alter {alterId}", null, null, null, null, null, null!);
            }
            else
            {
                alter = new BareAlter(
                    new(alterRow.GetValue<short>("id")),
                    alterRow.GetValue<string>("name"),
                    AvatarUrl.FromNullable(alterRow.GetValue<string?>("avatar_url")),
                    alterRow.GetValue<short?>("avatar_source").TryFromCode<AvatarSource>(out var src) ? src : null,
                    HexColor.FromNullable(alterRow.GetValue<string?>("color")),
                    alterRow.GetValue<string?>("pronouns"),
                    alterRow.GetValue<string?>("description"),
                    ResolveFields(alterRow.GetValue<IEnumerable<AlterFieldUdt>?>("fields"), definitions));
            }

            var timeStart = currentRow.GetValue<DateTimeOffset?>("time_start") ?? DateTimeOffset.UtcNow;
            var front = new FrontHistoryReadModel(
                new(frontId.Value),
                new(alterId),
                currentRow.GetValue<string?>("comment"),
                timeStart,
                null,
                new(normalizedSystemId));

            return new FrontActiveReadModel(alter, front, primaryAlterId == new AlterId(alterId));
        }, _options, cancellationToken, _logger);
    }

    public async Task<FrontHistoryReadModel?> GetHistoryEntryByFrontIdAsync(SystemId systemId, FrontId frontId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            var row = (await session.ExecuteAsync(new SimpleStatement(
                $"SELECT id, alter_id, comment, time_start, time_end FROM {keyspace}.fronts WHERE user_id = ? AND id = ? LIMIT 1 ALLOW FILTERING",
                normalizedSystemId,
                frontId.Value))).FirstOrDefault();

            if (row is null)
                return null;

            return new FrontHistoryReadModel(
                new(row.GetValue<Guid>("id")),
                new(row.GetValue<short>("alter_id")),
                row.GetValue<string?>("comment"),
                row.GetValue<DateTimeOffset>("time_start"),
                row.GetValue<DateTimeOffset?>("time_end"),
                new(normalizedSystemId));
        }, _options, cancellationToken, _logger);
    }

    public async Task<bool> EndByFrontIdAsync(SystemId systemId, FrontId frontId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            var current = await GetActiveFrontReferenceByFrontIdAsync(session, keyspace, normalizedSystemId, frontId.Value);
            if (current is null)
            {
                return false;
            }

            var primaryRow = (await session.ExecuteAsync(new SimpleStatement(
                $"SELECT primary_front_alter FROM {keyspace}.users WHERE id = ? LIMIT 1",
                normalizedSystemId))).FirstOrDefault();
            var primaryAlterId = primaryRow?.GetValue<short?>("primary_front_alter") is { } primaryShort
                ? new AlterId(primaryShort)
                : (AlterId?)null;

            var endedAt = DateTimeOffset.UtcNow;

            var endBatch = new BatchStatement();
            endBatch.Add(new SimpleStatement(
                $"UPDATE {keyspace}.fronts SET time_end = ?, updated_at = ? WHERE user_id = ? AND id = ? AND time_start = ?",
                endedAt,
                endedAt,
                normalizedSystemId,
                current.FrontId,
                current.StartedAt));
            endBatch.Add(new SimpleStatement(
                $"DELETE FROM {keyspace}.current_fronts WHERE user_id = ? AND alter_id = ?",
                normalizedSystemId,
                current.AlterId));
            // Maintain fronts_by_alter (update time_end)
            endBatch.Add(new SimpleStatement(
                $"UPDATE {keyspace}.fronts_by_alter SET time_end = ?, updated_at = ? WHERE user_id = ? AND alter_id = ? AND id = ? AND time_start = ?",
                endedAt, endedAt, normalizedSystemId, current.AlterId, current.FrontId, current.StartedAt));
            // Insert into fronts_by_time (only on close)
            endBatch.Add(new SimpleStatement(
                $"INSERT INTO {keyspace}.fronts_by_time (user_id, time_start, time_end, id, alter_id, comment, inserted_at, updated_at) VALUES (?, ?, ?, ?, ?, ?, ?, ?)",
                normalizedSystemId, current.StartedAt, endedAt, current.FrontId, current.AlterId, current.Comment, endedAt, endedAt));
            // Insert into fronts_by_end_time (only on close)
            endBatch.Add(new SimpleStatement(
                $"INSERT INTO {keyspace}.fronts_by_end_time (user_id, time_end, time_start, id, alter_id, comment, inserted_at, updated_at) VALUES (?, ?, ?, ?, ?, ?, ?, ?)",
                normalizedSystemId, endedAt, current.StartedAt, current.FrontId, current.AlterId, current.Comment, endedAt, endedAt));

            if (primaryAlterId == new AlterId(current.AlterId))
            {
                endBatch.Add(new SimpleStatement(
                    $"UPDATE {keyspace}.users SET primary_front_alter = null, updated_at = toTimestamp(now()) WHERE id = ?",
                    normalizedSystemId));
            }

            await session.ExecuteAsync(endBatch);
            return true;
        }, _options, cancellationToken, _logger);
    }

    public async Task<bool> DeleteFrontByIdAsync(SystemId systemId, FrontId frontId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            var row = (await session.ExecuteAsync(new SimpleStatement(
                $"SELECT alter_id, time_start, time_end FROM {keyspace}.fronts WHERE user_id = ? AND id = ? LIMIT 1 ALLOW FILTERING",
                normalizedSystemId,
                frontId.Value))).FirstOrDefault();

            if (row is null)
                return false;

            var alterId = new AlterId(row.GetValue<short>("alter_id"));
            var timeStart = row.GetValue<DateTimeOffset>("time_start");
            var timeEnd = row.GetValue<DateTimeOffset?>("time_end");
            var frontGuid = frontId.Value;

            var currentRow = await GetCurrentFrontRowAsync(session, keyspace, normalizedSystemId, alterId);
            var primaryRow = (await session.ExecuteAsync(new SimpleStatement(
                $"SELECT primary_front_alter FROM {keyspace}.users WHERE id = ? LIMIT 1",
                normalizedSystemId))).FirstOrDefault();
            var primaryAlterId = primaryRow?.GetValue<short?>("primary_front_alter") is { } primaryShort
                ? new AlterId(primaryShort)
                : (AlterId?)null;

            // Build batch for all delete/update operations
            var deleteBatch = new BatchStatement();
            
            // Remove from current_fronts if still active
            if (currentRow is not null && currentRow.FrontId == frontGuid)
            {
                deleteBatch.Add(new SimpleStatement(
                    $"DELETE FROM {keyspace}.current_fronts WHERE user_id = ? AND alter_id = ?",
                    normalizedSystemId, alterId.Value));
                
                if (primaryAlterId == alterId)
                {
                    deleteBatch.Add(new SimpleStatement(
                        $"UPDATE {keyspace}.users SET primary_front_alter = null, updated_at = toTimestamp(now()) WHERE id = ?",
                        normalizedSystemId));
                }
            }

            // Delete from base fronts table
            deleteBatch.Add(new SimpleStatement(
                $"DELETE FROM {keyspace}.fronts WHERE user_id = ? AND id = ? AND time_start = ?",
                normalizedSystemId, frontGuid, timeStart));
            // Delete from fronts_by_alter
            deleteBatch.Add(new SimpleStatement(
                $"DELETE FROM {keyspace}.fronts_by_alter WHERE user_id = ? AND alter_id = ? AND id = ? AND time_start = ?",
                normalizedSystemId, alterId.Value, frontGuid, timeStart));
            // Delete from fronts_by_time and fronts_by_end_time (only present if front was closed)
            if (timeEnd.HasValue)
            {
                deleteBatch.Add(new SimpleStatement(
                    $"DELETE FROM {keyspace}.fronts_by_time WHERE user_id = ? AND time_start = ? AND time_end = ? AND id = ?",
                    normalizedSystemId, timeStart, timeEnd.Value, frontGuid));
                deleteBatch.Add(new SimpleStatement(
                    $"DELETE FROM {keyspace}.fronts_by_end_time WHERE user_id = ? AND time_end = ? AND time_start = ? AND id = ?",
                    normalizedSystemId, timeEnd.Value, timeStart, frontGuid));
            }
            
            await session.ExecuteAsync(deleteBatch);

            return true;
        }, _options, cancellationToken, _logger);
    }

    public async Task<bool> UpdateCommentByFrontIdAsync(SystemId systemId, FrontId frontId, string comment, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            var current = await GetActiveFrontReferenceByFrontIdAsync(session, keyspace, normalizedSystemId, frontId.Value);
            if (current is null)
                return false;

            var commentBatch = new BatchStatement();
            commentBatch.Add(new SimpleStatement(
                $"UPDATE {keyspace}.current_fronts SET comment = ?, updated_at = toTimestamp(now()) WHERE user_id = ? AND alter_id = ?",
                comment,
                normalizedSystemId,
                current.AlterId
            ));
            commentBatch.Add(new SimpleStatement(
                $"UPDATE {keyspace}.fronts SET comment = ?, updated_at = toTimestamp(now()) WHERE user_id = ? AND id = ? AND time_start = ?",
                comment,
                normalizedSystemId,
                current.FrontId,
                current.StartedAt));
            // Maintain fronts_by_alter comment
            commentBatch.Add(new SimpleStatement(
                $"UPDATE {keyspace}.fronts_by_alter SET comment = ?, updated_at = toTimestamp(now()) WHERE user_id = ? AND alter_id = ? AND id = ? AND time_start = ?",
                comment, normalizedSystemId, current.AlterId, current.FrontId, current.StartedAt));
            await session.ExecuteAsync(commentBatch);
            return true;
        }, _options, cancellationToken, _logger);
    }

    private async Task<CurrentFrontRow?> GetActiveFrontReferenceByFrontIdAsync(
        ISession session,
        string keyspace,
        string normalizedSystemId,
        Guid frontId)
    {
        var frontRow = (await session.ExecuteAsync(new SimpleStatement(
            $"SELECT alter_id FROM {keyspace}.fronts WHERE user_id = ? AND id = ? LIMIT 1 ALLOW FILTERING",
            normalizedSystemId,
            frontId))).FirstOrDefault();

        if (frontRow is null)
        {
            return null;
        }

        var alterId = new AlterId(frontRow.GetValue<short>("alter_id"));
        var current = await GetCurrentFrontRowAsync(session, keyspace, normalizedSystemId, alterId);
        if (current is null || current.FrontId != frontId)
        {
            return null;
        }

        return current;
    }

    // CurrentFrontRow still carries a raw `short AlterId` because it mirrors the CQL
    // row shape one-to-one and is threaded through bind sites via `current.AlterId`.
    // Callers that need domain-side identity comparisons wrap with `new AlterId(...)` at
    // their read site; the storage boundary lives on this record.
    private async Task<CurrentFrontRow?> GetCurrentFrontRowAsync(
        ISession session,
        string keyspace,
        string normalizedSystemId,
        AlterId alterId)
    {
        var query = new SimpleStatement(
            $"SELECT id, alter_id, time_start, comment FROM {keyspace}.current_fronts WHERE user_id = ? AND alter_id = ? LIMIT 1",
            normalizedSystemId,
            alterId.Value);

        var row = (await session.ExecuteAsync(query)).FirstOrDefault();
        if (row is null)
        {
            return null;
        }

        var timeStart = row.GetValue<DateTimeOffset?>("time_start");
        if (timeStart is null)
        {
            // Corrupt current_fronts row (no time_start). Refuse to silently stamp
            // today's date into fronts_by_time on the next EndAsync; surface a
            // warning so we notice if this ever fires in production and let the
            // caller treat the row as if the front weren't current.
            _logger.LogWarning(
                "current_fronts row for user {SystemId} alter {AlterId} has null time_start; treating as not-current.",
                normalizedSystemId, alterId.Value);
            return null;
        }

        return new CurrentFrontRow(
            row.GetValue<Guid>("id"),
            row.GetValue<short>("alter_id"),
            timeStart.Value,
            row.GetValue<string?>("comment"));
    }

}
