using System.Diagnostics;
using Cassandra;
using Interfold.Alters.Contracts.Models;
using Interfold.Fronting.Contracts.Ids;
using Interfold.Fronting.Contracts.Models.Read;
using Interfold.Fronting.Domain.Abstractions.Repository;
using Interfold.Shared.Contracts.Configuration;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Domain.Abstractions.Repository;
using Interfold.Shared.Domain.Observability;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using static Interfold.Infrastructure.Scylla.Repository.ScyllaAlterRepository;

namespace Interfold.Infrastructure.Scylla.Repository;

public sealed class ScyllaFrontingRepository : IFrontingRepository
{
    private sealed record CurrentFrontRow(Guid FrontId, short AlterId, DateTimeOffset StartedAt, string? Comment);

    private readonly IScyllaSessionProvider _sessionProvider;
    private readonly IScyllaScopeResolver _scopeResolver;
    private readonly IScyllaKeyspaceResolver _keyspaceResolver;
    private readonly PersistenceConfiguration _options;
    private readonly ILogger<ScyllaFrontingRepository> _logger;
    private readonly ISettingsFieldRepository _settingsFields;

    public ScyllaFrontingRepository(
        IScyllaSessionProvider sessionProvider,
        IScyllaScopeResolver scopeResolver,
        IScyllaKeyspaceResolver keyspaceResolver,
        IOptions<PersistenceConfiguration> options,
        ILogger<ScyllaFrontingRepository> logger,
        ISettingsFieldRepository settingsFields
    )
    {
        _sessionProvider = sessionProvider;
        _scopeResolver = scopeResolver;
        _keyspaceResolver = keyspaceResolver;
        _options = options.Value;
        _logger = logger;
        _settingsFields = settingsFields;
    }

    public async Task<bool> IsFrontingAsync(SystemId systemId, AlterId alterId, CancellationToken cancellationToken = default)
    {
        return await _scopeResolver.ExecuteAsync(systemId, async scope =>
        {
            var (session, keyspace, normalizedSystemId) = scope;
            return await ScyllaExistsQueries.RowExistsAsync(session, keyspace, "current_fronts", "alter_id", normalizedSystemId, alterId.Value);
        }, cancellationToken);
    }

    public async Task<FrontId?> StartAsync(
        SystemId systemId,
        AlterId alterId,
        string? comment,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken = default
    )
    {
        return await _scopeResolver.ExecuteAsync<FrontId?>(systemId, async scope =>
        {
            var (session, keyspace, normalizedSystemId) = scope;
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
            // fronts_by_time / fronts_by_end_time land on close, not on start.
            await session.ExecuteAsync(startBatch);
            return new(frontGuid);
        }, cancellationToken);
    }

    public async Task<bool> EndAsync(SystemId systemId, AlterId alterId, DateTimeOffset endedAt, CancellationToken cancellationToken = default)
    {
        return await _scopeResolver.ExecuteAsync(systemId, async scope =>
        {
            var (session, keyspace, normalizedSystemId) = scope;

            var current = await GetCurrentFrontRowAsync(session, keyspace, normalizedSystemId, alterId);
            if (current is null)
            {
                return false;
            }

            // Prefetch primary_front_alter — closing it also clears the primary.
            var primaryAlterId = await ScyllaSharedQueries.LoadPrimaryFrontAlterAsync(session, keyspace, normalizedSystemId);

            var endBatch = new BatchStatement();
            ScyllaFrontingDenormalizedTable.AddFrontCloseStatements(
                endBatch,
                keyspace,
                normalizedSystemId,
                current.FrontId,
                alterId.Value,
                current.StartedAt,
                endedAt,
                current.Comment,
                primaryAlterId);
            
            await session.ExecuteAsync(endBatch);

            return true;
        }, cancellationToken);
    }

    public async Task<bool> SetPrimaryAsync(SystemId systemId, AlterId? alterId, CancellationToken cancellationToken = default)
    {
        return await _scopeResolver.ExecuteAsync(systemId, async scope =>
        {
            var (session, keyspace, normalizedSystemId) = scope;

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
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<FrontActiveReadModel>> ListActiveAsync(SystemId systemId, CancellationToken cancellationToken = default)
    {
        return await _scopeResolver.ExecuteAsync(systemId, async scope =>
        {
            var (session, keyspace, normalizedSystemId) = scope;

            // Register UDT mapping before SELECTing the fields UDT column.
            EnsureAlterFieldUdtMapping(session, keyspace);

            var activeTask = session.ExecuteAsync(new SimpleStatement(
                $"SELECT alter_id, id, comment, time_start FROM {keyspace}.current_fronts WHERE user_id = ?",
                normalizedSystemId
            ));

            var altersTask = session.ExecuteAsync(new SimpleStatement(
                $"SELECT id, name, avatar_url, avatar_source, description, color, fields, pronouns, pinned FROM {keyspace}.alters WHERE user_id = ?",
                normalizedSystemId));

            await Task.WhenAll(activeTask, altersTask);

            var primaryAlterId = await ScyllaSharedQueries.LoadPrimaryFrontAlterAsync(session, keyspace, normalizedSystemId);
            
            var rows = await activeTask;
            var alterRows = await altersTask;

            var definitions = await _settingsFields.ListAsync(systemId, cancellationToken);

            var alterById = alterRows.ToDictionary(
                row => new AlterId(row.GetValue<short>("id")),
                row => AlterRowMappers.MapBareAlter(row, definitions));

            return (IReadOnlyList<FrontActiveReadModel>)rows
                .Select(row =>
                {
                    FrontId frontId = new(row.GetValue<Guid>("id"));
                    AlterId alterId = new(row.GetValue<short>("alter_id"));
                    var timeStart = row.GetValue<DateTimeOffset?>("time_start") ?? DateTimeOffset.UtcNow;
                    var front = FrontingRowMappers.MapFrontHistoryReadModel(row, new(normalizedSystemId));

                    if (!alterById.TryGetValue(alterId, out var alter))
                    {
                        alter = BareAlter.CreatePlaceholder(alterId);
                    }

                    return new FrontActiveReadModel(alter, front, primaryAlterId == alterId);
                })
                .OrderByDescending(x => x.Front.TimeStart)
                .ToArray();
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<FrontActiveReadModel>> ListActiveGuardedAsync(
        SystemId systemId,
        SystemId? viewerSystemId,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var result = await _scopeResolver.ExecuteAsync(systemId, async scope =>
        {
            var (session, keyspace, normalizedSystemId) = scope;
            var friendshipLevel = await ScyllaSharedQueries.ResolveFriendshipLevelAsync(session, _keyspaceResolver, new(normalizedSystemId), viewerSystemId, _logger);

            var all = await ListActiveAsync(systemId, cancellationToken);
            if (all.Count == 0)
            {
                return (Total: 0, Visible: (IReadOnlyList<FrontActiveReadModel>)all, NormalizedSystemId: normalizedSystemId);
            }

            var securityRows = await session.ExecuteAsync(new SimpleStatement(
                $"SELECT id, security_level FROM {keyspace}.alters WHERE user_id = ?",
                normalizedSystemId));

            var visibilityByAlterId = securityRows.ToDictionary(
                row => new AlterId(row.GetValue<short>("id")),
                row => row.GetValue<short?>("security_level").FromStorage());

            var filtered = all
                .Where(front => visibilityByAlterId.TryGetValue(front.Alter.Id, out var level) && level.CanBeViewedBy(friendshipLevel))
                .ToArray();

            return (Total: all.Count, Visible: (IReadOnlyList<FrontActiveReadModel>)filtered, NormalizedSystemId: normalizedSystemId);
        }, cancellationToken);

        GuardedInstrumentation.RecordList(_logger, "fronting", nameof(ListActiveGuardedAsync), viewerSystemId, result.NormalizedSystemId, result.Total, result.Visible.Count, sw.Elapsed.TotalMilliseconds);
        return result.Visible;
    }

    public async Task<IReadOnlyList<FrontHistoryReadModel>> ListHistoryBetweenAsync(
        SystemId systemId,
        DateTimeOffset startInclusive,
        DateTimeOffset endInclusive,
        CancellationToken cancellationToken = default)
    {
        return await _scopeResolver.ExecuteAsync(systemId, async scope =>
        {
            var (session, keyspace, normalizedSystemId) = scope;

            var historyQuery = new SimpleStatement(
                $"SELECT id, alter_id, comment, time_start, time_end FROM {keyspace}.fronts_by_time WHERE user_id = ? AND time_start >= ? AND time_start <= ?",
                normalizedSystemId,
                startInclusive,
                endInclusive
            );

            var historicalRows = await session.ExecuteAsync(historyQuery);
            var historical = historicalRows
                .Select(row => FrontingRowMappers.MapFrontHistoryReadModel(row, new(normalizedSystemId), row.GetValue<DateTimeOffset?>("time_end")))
                .Where(x => x.TimeEnd != null)
                .ToList();

            return (IReadOnlyList<FrontHistoryReadModel>)historical
                .DistinctBy(x => x.Id)
                .OrderByDescending(x => x.TimeStart)
                .ToArray();
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<FrontHistoryReadModel>> ListAllAsync(SystemId systemId, CancellationToken cancellationToken = default)
    {
        return await _scopeResolver.ExecuteAsync(systemId, async scope =>
        {
            var (session, keyspace, normalizedSystemId) = scope;

            // Base `fronts` partition key = user_id → no ALLOW FILTERING needed. Corrupt rows
            // (null time_start) fall through the row-mapper's DateTimeOffset.UtcNow fallback;
            // drop them here before they leak into export output.
            var query = new SimpleStatement(
                $"SELECT id, alter_id, comment, time_start, time_end FROM {keyspace}.fronts WHERE user_id = ?",
                normalizedSystemId);

            var rows = await session.ExecuteAsync(query);
            return (IReadOnlyList<FrontHistoryReadModel>)rows
                .Where(row => row.GetValue<DateTimeOffset?>("time_start") is not null)
                .Select(row => FrontingRowMappers.MapFrontHistoryReadModel(row, new(normalizedSystemId), row.GetValue<DateTimeOffset?>("time_end")))
                .DistinctBy(x => x.Id)
                .OrderByDescending(x => x.TimeStart)
                .ToArray();
        }, cancellationToken);
    }

    public async Task<FrontActiveReadModel?> GetActiveByFrontIdAsync(SystemId systemId, FrontId frontId, CancellationToken cancellationToken = default)
    {
        return await _scopeResolver.ExecuteAsync(systemId, async scope =>
        {
            var (session, keyspace, normalizedSystemId) = scope;

            var frontRow = (await session.ExecuteAsync(new SimpleStatement(
                $"SELECT alter_id FROM {keyspace}.fronts WHERE user_id = ? AND id = ? LIMIT 1 ALLOW FILTERING",
                normalizedSystemId,
                frontId.Value))).FirstOrDefault();

            if (frontRow is null)
                return null;

            var alterId = frontRow.GetValue<short>("alter_id");

            EnsureAlterFieldUdtMapping(session, keyspace);
            var definitions = await _settingsFields.ListAsync(systemId, cancellationToken);

            var currentTask = session.ExecuteAsync(new SimpleStatement(
                $"SELECT id, alter_id, comment, time_start FROM {keyspace}.current_fronts WHERE user_id = ? AND alter_id = ? LIMIT 1",
                normalizedSystemId, alterId));

            var alterTask = session.ExecuteAsync(new SimpleStatement(
                $"SELECT id, name, avatar_url, avatar_source, description, color, fields, pronouns, pinned FROM {keyspace}.alters WHERE user_id = ? AND id = ? LIMIT 1",
                normalizedSystemId, alterId));

            await Task.WhenAll(currentTask, alterTask);

            var currentRow = (await currentTask).FirstOrDefault();
            if (currentRow is null || currentRow.GetValue<Guid>("id") != frontId.Value)
                return null;

            var alterRow = (await alterTask).FirstOrDefault();
            var primaryAlterId = await ScyllaSharedQueries.LoadPrimaryFrontAlterAsync(session, keyspace, normalizedSystemId);

            BareAlter alter;
            if (alterRow is null)
            {
                alter = BareAlter.CreatePlaceholder(new(alterId));
            }
            else
            {
                alter = AlterRowMappers.MapBareAlter(alterRow, definitions);
            }

            var front = FrontingRowMappers.MapFrontHistoryReadModel(currentRow, new(normalizedSystemId));

            return new FrontActiveReadModel(alter, front, primaryAlterId == new AlterId(alterId));
        }, cancellationToken);
    }

    public async Task<FrontHistoryReadModel?> GetHistoryEntryByFrontIdAsync(SystemId systemId, FrontId frontId, CancellationToken cancellationToken = default)
    {
        return await _scopeResolver.ExecuteAsync(systemId, async scope =>
        {
            var (session, keyspace, normalizedSystemId) = scope;

            var row = (await session.ExecuteAsync(new SimpleStatement(
                $"SELECT id, alter_id, comment, time_start, time_end FROM {keyspace}.fronts WHERE user_id = ? AND id = ? LIMIT 1 ALLOW FILTERING",
                normalizedSystemId,
                frontId.Value))).FirstOrDefault();

            if (row is null)
                return null;

            return FrontingRowMappers.MapFrontHistoryReadModel(row, new(normalizedSystemId), row.GetValue<DateTimeOffset?>("time_end"));
        }, cancellationToken);
    }

    public async Task<bool> EndByFrontIdAsync(SystemId systemId, FrontId frontId, CancellationToken cancellationToken = default)
    {
        return await _scopeResolver.ExecuteAsync(systemId, async scope =>
        {
            var (session, keyspace, normalizedSystemId) = scope;

            var current = await GetActiveFrontReferenceByFrontIdAsync(session, keyspace, normalizedSystemId, frontId.Value);
            if (current is null)
            {
                return false;
            }

            var primaryAlterId = await ScyllaSharedQueries.LoadPrimaryFrontAlterAsync(session, keyspace, normalizedSystemId);

            var endedAt = DateTimeOffset.UtcNow;

            var endBatch = new BatchStatement();
            ScyllaFrontingDenormalizedTable.AddFrontCloseStatements(
                endBatch,
                keyspace,
                normalizedSystemId,
                current.FrontId,
                current.AlterId,
                current.StartedAt,
                endedAt,
                current.Comment,
                primaryAlterId);

            await session.ExecuteAsync(endBatch);
            return true;
        }, cancellationToken);
    }

    public async Task<bool> DeleteFrontByIdAsync(SystemId systemId, FrontId frontId, CancellationToken cancellationToken = default)
    {
        return await _scopeResolver.ExecuteAsync(systemId, async scope =>
        {
            var (session, keyspace, normalizedSystemId) = scope;

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
            var primaryAlterId = await ScyllaSharedQueries.LoadPrimaryFrontAlterAsync(session, keyspace, normalizedSystemId);

            var deleteBatch = new BatchStatement();

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

            deleteBatch.Add(new SimpleStatement(
                $"DELETE FROM {keyspace}.fronts WHERE user_id = ? AND id = ? AND time_start = ?",
                normalizedSystemId, frontGuid, timeStart));
            deleteBatch.Add(new SimpleStatement(
                $"DELETE FROM {keyspace}.fronts_by_alter WHERE user_id = ? AND alter_id = ? AND id = ? AND time_start = ?",
                normalizedSystemId, alterId.Value, frontGuid, timeStart));
            // *_by_time siblings only exist for closed fronts.
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
        }, cancellationToken);
    }

    public async Task<bool> UpdateCommentByFrontIdAsync(SystemId systemId, FrontId frontId, string comment, CancellationToken cancellationToken = default)
    {
        return await _scopeResolver.ExecuteAsync(systemId, async scope =>
        {
            var (session, keyspace, normalizedSystemId) = scope;

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
            commentBatch.Add(new SimpleStatement(
                $"UPDATE {keyspace}.fronts_by_alter SET comment = ?, updated_at = toTimestamp(now()) WHERE user_id = ? AND alter_id = ? AND id = ? AND time_start = ?",
                comment, normalizedSystemId, current.AlterId, current.FrontId, current.StartedAt));
            await session.ExecuteAsync(commentBatch);
            return true;
        }, cancellationToken);
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

    // Raw short AlterId mirrors the CQL row shape; callers wrap in new AlterId(...) at read time.
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
            // Corrupt row — refuse to silently stamp today's date into fronts_by_time on
            // the next EndAsync. Treat as not-current and warn loudly.
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
