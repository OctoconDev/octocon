using Cassandra;
using Interfold.Shared.Contracts.Configuration;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models.Commands;
using Interfold.Shared.Contracts.Models.Read;
using Interfold.Shared.Domain.Abstractions.Repository;
using Interfold.Infrastructure.Persistence;
using Microsoft.Extensions.Options;

namespace Interfold.Infrastructure.Scylla.Repository;

public sealed class ScyllaJournalRepository : IJournalRepository
{
    private readonly IScyllaSessionProvider _sessionProvider;
    private readonly IScyllaScopeResolver _scopeResolver;
    private readonly IScyllaKeyspaceResolver _keyspaceResolver;
    private readonly PersistenceConfiguration _options;

    public ScyllaJournalRepository(
        IScyllaSessionProvider sessionProvider,
        IScyllaScopeResolver scopeResolver,
        IScyllaKeyspaceResolver keyspaceResolver,
        IOptions<PersistenceConfiguration> options)
    {
        _sessionProvider = sessionProvider;
        _scopeResolver = scopeResolver;
        _keyspaceResolver = keyspaceResolver;
        _options = options.Value;
    }

    public async Task<EntryId?> CreateGlobalAsync(SystemId systemId, CreateGlobalJournalEntryCommand command, CancellationToken cancellationToken = default)
    {
        return await _scopeResolver.ExecuteAsync<EntryId?>(systemId, async scope =>
        {
            var (session, keyspace, normalizedSystemId) = scope;
            var entryId = Guid.NewGuid();

            var insert = new SimpleStatement(
                $"INSERT INTO {keyspace}.global_journals (user_id, id, title, content, color, pinned, locked, inserted_at, updated_at) VALUES (?, ?, ?, ?, ?, ?, ?, toTimestamp(now()), toTimestamp(now()))",
                normalizedSystemId,
                entryId,
                command.Title,
                null,
                null,
                false,
                false
            );

            await session.ExecuteAsync(insert);
            return new(entryId);
        }, cancellationToken);
    }

    public async Task<bool> ExistsGlobalAsync(SystemId systemId, EntryId entryId, CancellationToken cancellationToken = default)
    {
        return await _scopeResolver.ExecuteAsync(systemId, async scope =>
            await ExistsGlobalCoreAsync(scope, entryId), cancellationToken);
    }

    private static Task<bool> ExistsGlobalCoreAsync(ScyllaScope scope, EntryId entryId)
        => ScyllaExistsQueries.RowExistsAsync(scope.Session, scope.Keyspace, "global_journals", "id", scope.NormalizedSystemId, entryId.Value);

    public async Task<bool> UpdateGlobalAsync(SystemId systemId, UpdateGlobalJournalEntryCommand command, CancellationToken cancellationToken = default)
    {
        return await _scopeResolver.ExecuteAsync(systemId, async scope =>
        {
            var (session, keyspace, normalizedSystemId) = scope;

            var exists = await ExistsGlobalCoreAsync(scope, command.EntryId);
            if (!exists)
                return false;

            var updateBatch = new BatchStatement();

            if (command.Title is not null)
            {
                var q = new SimpleStatement(
                    $"UPDATE {keyspace}.global_journals SET title = ?, updated_at = toTimestamp(now()) WHERE user_id = ? AND id = ?",
                    command.Title,
                    normalizedSystemId,
                    command.EntryId.Value
                );
                updateBatch.Add(q);
            }

            if (command.Content is not null)
            {
                var q = new SimpleStatement(
                    $"UPDATE {keyspace}.global_journals SET content = ?, updated_at = toTimestamp(now()) WHERE user_id = ? AND id = ?",
                    command.Content,
                    normalizedSystemId,
                    command.EntryId.Value
                );
                updateBatch.Add(q);
            }

            if (command.Color is { } color)
            {
                var q = new SimpleStatement(
                    $"UPDATE {keyspace}.global_journals SET color = ?, updated_at = toTimestamp(now()) WHERE user_id = ? AND id = ?",
                    color.Value,
                    normalizedSystemId,
                    command.EntryId.Value
                );
                updateBatch.Add(q);
            }

            if (!updateBatch.IsEmpty)
            {
                await session.ExecuteAsync(updateBatch);
            }

            return true;
        }, cancellationToken);
    }

    public async Task<bool> DeleteGlobalAsync(SystemId systemId, EntryId entryId, CancellationToken cancellationToken = default)
    {
        return await _scopeResolver.ExecuteAsync(systemId, async scope =>
        {
            var (session, keyspace, normalizedSystemId) = scope;

            var exists = await ExistsGlobalCoreAsync(scope, entryId);
            if (!exists)
                return false;

            var deleteBatch = new BatchStatement();
            deleteBatch.Add(new SimpleStatement(
                $"DELETE FROM {keyspace}.global_journals WHERE user_id = ? AND id = ?",
                normalizedSystemId,
                entryId.Value
            ));
            deleteBatch.Add(new SimpleStatement(
                $"DELETE FROM {keyspace}.global_journal_alters WHERE user_id = ? AND global_journal_id = ?",
                normalizedSystemId,
                entryId.Value
            ));
            await session.ExecuteAsync(deleteBatch);

            return true;
        }, cancellationToken);
    }

    public Task<bool> SetGlobalLockedAsync(SystemId systemId, EntryId entryId, bool locked, CancellationToken cancellationToken = default)
        => SetGlobalFlagAsync(systemId, entryId, JournalFlag.Locked, locked, cancellationToken);

    public Task<bool> SetGlobalPinnedAsync(SystemId systemId, EntryId entryId, bool pinned, CancellationToken cancellationToken = default)
        => SetGlobalFlagAsync(systemId, entryId, JournalFlag.Pinned, pinned, cancellationToken);

    private async Task<bool> SetGlobalFlagAsync(SystemId systemId, EntryId entryId, JournalFlag flag, bool value, CancellationToken cancellationToken)
    {
        return await _scopeResolver.ExecuteAsync(systemId, async scope =>
        {
            var exists = await ExistsGlobalCoreAsync(scope, entryId);
            if (!exists)
                return false;

            var upsert = new SimpleStatement(
                $"UPDATE {scope.Keyspace}.global_journals SET {FlagToColumn(flag)} = ?, updated_at = toTimestamp(now()) WHERE user_id = ? AND id = ?",
                value,
                scope.NormalizedSystemId,
                entryId.Value
            );
            await scope.Session.ExecuteAsync(upsert);

            return true;
        }, cancellationToken);
    }

    public async Task<bool> AttachGlobalAlterAsync(SystemId systemId, EntryId entryId, AlterId alterId, CancellationToken cancellationToken = default)
    {
        return await _scopeResolver.ExecuteAsync(systemId, async scope =>
        {
            var (session, keyspace, normalizedSystemId) = scope;

            var exists = await ExistsGlobalCoreAsync(scope, entryId);
            if (!exists)
                return false;

            var insert = new SimpleStatement(
                $"INSERT INTO {keyspace}.global_journal_alters (user_id, global_journal_id, alter_id, inserted_at, updated_at) VALUES (?, ?, ?, toTimestamp(now()), toTimestamp(now()))",
                normalizedSystemId,
                entryId.Value,
                alterId.Value
            );
            await session.ExecuteAsync(insert);

            return true;
        }, cancellationToken);
    }

    public async Task<bool> DetachGlobalAlterAsync(SystemId systemId, EntryId entryId, AlterId alterId, CancellationToken cancellationToken = default)
    {
        return await _scopeResolver.ExecuteAsync(systemId, async scope =>
        {
            var (session, keyspace, normalizedSystemId) = scope;

            var edgeExistsQuery = new SimpleStatement(
                $"SELECT alter_id FROM {keyspace}.global_journal_alters WHERE user_id = ? AND global_journal_id = ? AND alter_id = ? LIMIT 1",
                normalizedSystemId,
                entryId.Value,
                alterId.Value
            );

            var edgeRows = await session.ExecuteAsync(edgeExistsQuery);
            if (!edgeRows.Any())
                return false;

            var delete = new SimpleStatement(
                $"DELETE FROM {keyspace}.global_journal_alters WHERE user_id = ? AND global_journal_id = ? AND alter_id = ?",
                normalizedSystemId,
                entryId.Value,
                alterId.Value
            );
            await session.ExecuteAsync(delete);

            return true;
        }, cancellationToken);
    }

    public async Task<EntryId?> CreateAlterAsync(SystemId systemId, CreateAlterJournalEntryCommand command, CancellationToken cancellationToken = default)
    {
        return await _scopeResolver.ExecuteAsync<EntryId?>(systemId, async scope =>
        {
            var (session, keyspace, normalizedSystemId) = scope;
            var entryId = Guid.NewGuid();

            var timestamp = command.CreatedAt.ToUniversalTime();

            var insert = new SimpleStatement(
                $"INSERT INTO {keyspace}.alter_journals (user_id, id, alter_id, title, content, color, pinned, locked, inserted_at, updated_at) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)",
                normalizedSystemId,
                entryId,
                command.AlterId.Value,
                command.Title,
                null,
                null,
                false,
                false,
                timestamp,
                timestamp
            );

            var insertLookup = new SimpleStatement(
                $"INSERT INTO {keyspace}.alter_journals_by_alter (user_id, alter_id, id, title, content, color, pinned, locked, inserted_at, updated_at) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)",
                normalizedSystemId,
                command.AlterId.Value,
                entryId,
                command.Title,
                null,
                null,
                false,
                false,
                timestamp,
                timestamp
            );

            var batch = new BatchStatement();
            batch.Add(insert);
            batch.Add(insertLookup);
            await session.ExecuteAsync(batch);
            return new(entryId);
        }, cancellationToken);
    }

    public async Task<AlterJournalRef?> GetAlterRefAsync(SystemId systemId, EntryId entryId, CancellationToken cancellationToken = default)
    {
        return await _scopeResolver.ExecuteAsync(systemId, async scope =>
            await GetAlterRefCoreAsync(scope, entryId, cancellationToken), cancellationToken);
    }

    // In-scope variant used by the alter-scoped writers so we don't stack a second
    // IScyllaScopeResolver retry envelope inside the outer one.
    private static async Task<AlterJournalRef?> GetAlterRefCoreAsync(
        ScyllaScope scope, EntryId entryId, CancellationToken cancellationToken)
    {
        var query = new SimpleStatement(
            $"SELECT id, alter_id FROM {scope.Keyspace}.alter_journals WHERE user_id = ? AND id = ? ALLOW FILTERING",
            scope.NormalizedSystemId,
            entryId.Value
        );

        var row = (await scope.Session.ExecuteAsync(query)).FirstOrDefault();
        return row is null
            ? null
            : new AlterJournalRef(new(row.GetValue<Guid>("id")), new(row.GetValue<short>("alter_id")));
    }

    public async Task<bool> UpdateAlterAsync(SystemId systemId, UpdateAlterJournalEntryCommand command, CancellationToken cancellationToken = default)
    {
        return await _scopeResolver.ExecuteAsync(systemId, async scope =>
        {
            var reference = await GetAlterRefCoreAsync(scope, command.EntryId, cancellationToken);
            if (reference is null)
                return false;

            var timestamp = command.UpdatedAt.ToUniversalTime();
            var updateBatch = new BatchStatement();

            if (command.Title is not null)
            {
                updateBatch.Add(new SimpleStatement(
                    $"UPDATE {scope.Keyspace}.alter_journals SET title = ?, updated_at = ? WHERE user_id = ? AND id = ? AND alter_id = ?",
                    command.Title, timestamp, scope.NormalizedSystemId, reference.EntryId.Value, reference.AlterId.Value));
                updateBatch.Add(new SimpleStatement(
                    $"UPDATE {scope.Keyspace}.alter_journals_by_alter SET title = ?, updated_at = ? WHERE user_id = ? AND alter_id = ? AND id = ?",
                    command.Title, timestamp, scope.NormalizedSystemId, reference.AlterId.Value, reference.EntryId.Value));
            }

            if (command.Content is not null)
            {
                updateBatch.Add(new SimpleStatement(
                    $"UPDATE {scope.Keyspace}.alter_journals SET content = ?, updated_at = ? WHERE user_id = ? AND id = ? AND alter_id = ?",
                    command.Content, timestamp, scope.NormalizedSystemId, reference.EntryId.Value, reference.AlterId.Value));
                updateBatch.Add(new SimpleStatement(
                    $"UPDATE {scope.Keyspace}.alter_journals_by_alter SET content = ?, updated_at = ? WHERE user_id = ? AND alter_id = ? AND id = ?",
                    command.Content, timestamp, scope.NormalizedSystemId, reference.AlterId.Value, reference.EntryId.Value));
            }

            if (command.Color is not null)
            {
                updateBatch.Add(new SimpleStatement(
                    $"UPDATE {scope.Keyspace}.alter_journals SET color = ?, updated_at = ? WHERE user_id = ? AND id = ? AND alter_id = ?",
                    command.Color?.Value, timestamp, scope.NormalizedSystemId, reference.EntryId.Value, reference.AlterId.Value));
                updateBatch.Add(new SimpleStatement(
                    $"UPDATE {scope.Keyspace}.alter_journals_by_alter SET color = ?, updated_at = ? WHERE user_id = ? AND alter_id = ? AND id = ?",
                    command.Color?.Value, timestamp, scope.NormalizedSystemId, reference.AlterId.Value, reference.EntryId.Value));
            }

            if (!updateBatch.IsEmpty)
                await scope.Session.ExecuteAsync(updateBatch);

            return true;
        }, cancellationToken);
    }

    public async Task<bool> DeleteAlterAsync(SystemId systemId, EntryId entryId, CancellationToken cancellationToken = default)
    {
        return await _scopeResolver.ExecuteAsync(systemId, async scope =>
        {
            var reference = await GetAlterRefCoreAsync(scope, entryId, cancellationToken);
            if (reference is null)
                return false;

            var delete = new BatchStatement();
            delete.Add(new SimpleStatement(
                $"DELETE FROM {scope.Keyspace}.alter_journals WHERE user_id = ? AND id = ? AND alter_id = ?",
                scope.NormalizedSystemId,
                reference.EntryId.Value,
                reference.AlterId.Value
            ));
            delete.Add(new SimpleStatement(
                $"DELETE FROM {scope.Keyspace}.alter_journals_by_alter WHERE user_id = ? AND alter_id = ? AND id = ?",
                scope.NormalizedSystemId,
                reference.AlterId.Value,
                reference.EntryId.Value
            ));
            await scope.Session.ExecuteAsync(delete);

            return true;
        }, cancellationToken);
    }

    public Task<bool> SetAlterLockedAsync(SystemId systemId, EntryId entryId, bool locked, CancellationToken cancellationToken = default)
        => SetAlterFlagAsync(systemId, entryId, JournalFlag.Locked, locked, cancellationToken);

    public Task<bool> SetAlterPinnedAsync(SystemId systemId, EntryId entryId, bool pinned, CancellationToken cancellationToken = default)
        => SetAlterFlagAsync(systemId, entryId, JournalFlag.Pinned, pinned, cancellationToken);

    private async Task<bool> SetAlterFlagAsync(SystemId systemId, EntryId entryId, JournalFlag flag, bool value, CancellationToken cancellationToken)
    {
        return await _scopeResolver.ExecuteAsync(systemId, async scope =>
        {
            var reference = await GetAlterRefCoreAsync(scope, entryId, cancellationToken);
            if (reference is null)
                return false;

            var column = FlagToColumn(flag);
            var batch = new BatchStatement();
            batch.Add(new SimpleStatement(
                $"UPDATE {scope.Keyspace}.alter_journals SET {column} = ?, updated_at = toTimestamp(now()) WHERE user_id = ? AND id = ? AND alter_id = ?",
                value, scope.NormalizedSystemId, reference.EntryId.Value, reference.AlterId.Value));
            batch.Add(new SimpleStatement(
                $"UPDATE {scope.Keyspace}.alter_journals_by_alter SET {column} = ?, updated_at = toTimestamp(now()) WHERE user_id = ? AND alter_id = ? AND id = ?",
                value, scope.NormalizedSystemId, reference.AlterId.Value, reference.EntryId.Value));
            await scope.Session.ExecuteAsync(batch);

            return true;
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<AlterJournalReadModel>> ListAlterAsync(SystemId systemId, AlterId alterId, CancellationToken cancellationToken = default)
    {
        return await _scopeResolver.ExecuteAsync(systemId, async scope =>
        {
            var (session, keyspace, normalizedSystemId) = scope;

            var query = new SimpleStatement(
                $"SELECT id, user_id, alter_id, title, content, color, pinned, locked, inserted_at, updated_at FROM {keyspace}.alter_journals_by_alter WHERE user_id = ? AND alter_id = ?",
                normalizedSystemId,
                alterId.Value
            );

            var rows = await session.ExecuteAsync(query);
            return rows
                .Select(row => JournalRowMappers.MapAlterJournalReadModel(row))
                .OrderByDescending(e => e.InsertedAt)
                .ToArray();
        }, cancellationToken);
    }

    public async Task<AlterJournalReadModel?> GetAlterAsync(SystemId systemId, EntryId entryId, CancellationToken cancellationToken = default)
    {
        return await _scopeResolver.ExecuteAsync(systemId, async scope =>
        {
            var (session, keyspace, normalizedSystemId) = scope;

            var query = new SimpleStatement(
                $"SELECT id, user_id, alter_id, title, content, color, pinned, locked, inserted_at, updated_at FROM {keyspace}.alter_journals WHERE user_id = ? AND id = ? ALLOW FILTERING",
                normalizedSystemId,
                entryId.Value
            );

            var row = (await session.ExecuteAsync(query)).FirstOrDefault();
            return row is null
                ? null
                : JournalRowMappers.MapAlterJournalReadModel(row);
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<JournalReadModel>> ListGlobalAsync(SystemId systemId, CancellationToken cancellationToken = default)
    {
        return await _scopeResolver.ExecuteAsync(systemId, async scope =>
        {
            var (session, keyspace, normalizedSystemId) = scope;

            var entriesQuery = new SimpleStatement(
                $"SELECT id, user_id, title, content, color, pinned, locked, inserted_at, updated_at FROM {keyspace}.global_journals WHERE user_id = ?",
                normalizedSystemId
            );
            var entryRows = await session.ExecuteAsync(entriesQuery);

            var result = new List<JournalReadModel>();
            foreach (var row in entryRows)
            {
                var id = row.GetValue<Guid>("id");
                var altersQuery = new SimpleStatement(
                    $"SELECT alter_id FROM {keyspace}.global_journal_alters WHERE user_id = ? AND global_journal_id = ?",
                    normalizedSystemId,
                    id
                );
                var alterRows = await session.ExecuteAsync(altersQuery);
                var alterIds = alterRows.Select(r => new AlterId(r.GetValue<short>("alter_id"))).ToArray();

                result.Add(JournalRowMappers.MapJournalReadModel(row, alterIds));
            }

            // Sort by wire form (lowercase "N" hex); Guid.CompareTo would reorder differently.
            return (IReadOnlyList<JournalReadModel>)result
                .OrderByDescending(e => e.Id.Value.ToString("N"), StringComparer.Ordinal)
                .ToArray();
        }, cancellationToken);
    }

    public async Task<JournalReadModel?> GetGlobalAsync(SystemId systemId, EntryId entryId, CancellationToken cancellationToken = default)
    {
        return await _scopeResolver.ExecuteAsync(systemId, async scope =>
        {
            var (session, keyspace, normalizedSystemId) = scope;

            var entryQuery = new SimpleStatement(
                $"SELECT id, user_id, title, content, color, pinned, locked, inserted_at, updated_at FROM {keyspace}.global_journals WHERE user_id = ? AND id = ? LIMIT 1",
                normalizedSystemId,
                entryId.Value
            );
            var entryRow = (await session.ExecuteAsync(entryQuery)).FirstOrDefault();
            if (entryRow is null)
                return null;

            var altersQuery = new SimpleStatement(
                $"SELECT alter_id FROM {keyspace}.global_journal_alters WHERE user_id = ? AND global_journal_id = ?",
                normalizedSystemId,
                entryId.Value
            );
            var alterIds = (await session.ExecuteAsync(altersQuery))
                .Select(r => new AlterId(r.GetValue<short>("alter_id")))
                .ToArray();

            return JournalRowMappers.MapJournalReadModel(entryRow, alterIds);
        }, cancellationToken);
    }

    public async Task<int> DeleteAllForAlterAsync(SystemId systemId, AlterId alterId, CancellationToken cancellationToken = default)
    {
        return await _scopeResolver.ExecuteAsync(systemId, async scope =>
        {
            var (session, keyspace, normalizedSystemId) = scope;

            // Single-partition read on (user_id, alter_id).
            var listQuery = new SimpleStatement(
                $"SELECT id FROM {keyspace}.alter_journals_by_alter WHERE user_id = ? AND alter_id = ?",
                normalizedSystemId,
                alterId.Value);
            var entryIds = (await session.ExecuteAsync(listQuery))
                .Select(r => r.GetValue<Guid>("id"))
                .ToArray();

            // Client-side filter — the partition is small, ALLOW FILTERING isn't worth the
            // server-side burn for the same row reads.
            var attachmentsQuery = new SimpleStatement(
                $"SELECT global_journal_id, alter_id FROM {keyspace}.global_journal_alters WHERE user_id = ?",
                normalizedSystemId);
            var attachedGlobals = (await session.ExecuteAsync(attachmentsQuery))
                .Where(r => new AlterId(r.GetValue<short>("alter_id")) == alterId)
                .Select(r => r.GetValue<Guid>("global_journal_id"))
                .ToArray();

            if (entryIds.Length == 0 && attachedGlobals.Length == 0)
            {
                return 0;
            }

            // All deletes share user_id, so this is a single-coordinator batch.
            var batch = new BatchStatement();

            foreach (var entryId in entryIds)
            {
                batch.Add(new SimpleStatement(
                    $"DELETE FROM {keyspace}.alter_journals WHERE user_id = ? AND id = ? AND alter_id = ?",
                    normalizedSystemId,
                    entryId,
                    alterId.Value));
                batch.Add(new SimpleStatement(
                    $"DELETE FROM {keyspace}.alter_journals_by_alter WHERE user_id = ? AND alter_id = ? AND id = ?",
                    normalizedSystemId,
                    alterId.Value,
                    entryId));
            }

            foreach (var globalJournalId in attachedGlobals)
            {
                batch.Add(new SimpleStatement(
                    $"DELETE FROM {keyspace}.global_journal_alters WHERE user_id = ? AND global_journal_id = ? AND alter_id = ?",
                    normalizedSystemId,
                    globalJournalId,
                    alterId.Value));
            }

            await session.ExecuteAsync(batch);
            return entryIds.Length;
        }, cancellationToken);
    }

    // Closed enum instead of a raw string column parameter so no caller can slip a
    // CQL-injection-adjacent identifier into the UPDATE.
    private enum JournalFlag
    {
        Locked,
        Pinned,
    }

    private static string FlagToColumn(JournalFlag flag) => flag switch
    {
        JournalFlag.Locked => "locked",
        JournalFlag.Pinned => "pinned",
        _ => throw new ArgumentOutOfRangeException(nameof(flag), flag, "Unknown JournalFlag."),
    };
}
