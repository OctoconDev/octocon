using System.Text.Json;
using Cassandra;
using Interfold.Contracts.Configuration;
using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Read;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Infrastructure.Persistence;
using Microsoft.Extensions.Options;

namespace Interfold.Infrastructure.Scylla.Repository;

public sealed class ScyllaPollRepository : IPollRepository
{
    private readonly IScyllaSessionProvider _sessionProvider;
    private readonly IScyllaKeyspaceResolver _keyspaceResolver;
    private readonly PersistenceConfiguration _options;

    public ScyllaPollRepository(
        IScyllaSessionProvider sessionProvider,
        IScyllaKeyspaceResolver keyspaceResolver,
        IOptions<PersistenceConfiguration> options)
    {
        _sessionProvider = sessionProvider;
        _keyspaceResolver = keyspaceResolver;
        _options = options.Value;
    }

    public async Task<IReadOnlyList<PollReadModel>> ListAsync(SystemId systemId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            var query = new SimpleStatement(
                $"SELECT id, user_id, title, description, type, data, time_end, inserted_at, updated_at FROM {keyspace}.polls WHERE user_id = ?",
                normalizedSystemId
            );

            var rows = await session.ExecuteAsync(query);
            // VERIFIED: 2026-03-17 Elixir polls.ex get_polls() has no explicit sort → database order (ascending). Matches C# OrderBy.
            // Sort key is the wire form (lowercase "N" hex) to keep list ordering byte-identical
            // to the historic string-backed PollId — Guid.CompareTo bytewise reorders differently.
            return rows.Select(ToReadModel).OrderBy(p => p.Id.Value.ToString("N"), StringComparer.Ordinal).ToList();
        }, _options, cancellationToken);
    }

    public async Task<PollReadModel?> GetAsync(SystemId systemId, PollId pollId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            var query = new SimpleStatement(
                $"SELECT id, user_id, title, description, type, data, time_end, inserted_at, updated_at FROM {keyspace}.polls WHERE user_id = ? AND id = ? LIMIT 1",
                normalizedSystemId,
                pollId.Value
            );

            var row = (await session.ExecuteAsync(query)).FirstOrDefault();
            return row is null ? null : ToReadModel(row);
        }, _options, cancellationToken);
    }

    public async Task<PollId?> CreateAsync(SystemId systemId, CreatePollCommand command, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync<PollId?>(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);
            var pollGuid = Guid.NewGuid();

            var insert = new SimpleStatement(
                $"INSERT INTO {keyspace}.polls (user_id, id, title, description, type, data, time_end, inserted_at, updated_at) VALUES (?, ?, ?, ?, ?, ?, ?, ?, toTimestamp(now()))",
                normalizedSystemId,
                pollGuid,
                command.Title,
                command.Description,
                (short)command.Type,
                "{}",
                command.TimeEnd,
                command.InsertedAtUtc
            );

            await session.ExecuteAsync(insert);
            return new(pollGuid);
        }, _options, cancellationToken);
    }

    public async Task<bool> ExistsAsync(SystemId systemId, PollId pollId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            var query = new SimpleStatement(
                $"SELECT id FROM {keyspace}.polls WHERE user_id = ? AND id = ? LIMIT 1",
                normalizedSystemId,
                pollId.Value
            );

            var rows = await session.ExecuteAsync(query);
            return rows.Any();
        }, _options, cancellationToken);
    }

    public async Task<bool> UpdateAsync(SystemId systemId, UpdatePollCommand command, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            var exists = await ExistsAsync(session, keyspace, normalizedSystemId, command.Id.Value);
            if (!exists)
                return false;

            var updateBatch = new BatchStatement();

            if (command.Title is not null)
            {
                updateBatch.Add(new SimpleStatement(
                    $"UPDATE {keyspace}.polls SET title = ?, updated_at = toTimestamp(now()) WHERE user_id = ? AND id = ?",
                    command.Title,
                    normalizedSystemId,
                    command.Id.Value));
            }

            if (command.Description is not null)
            {
                updateBatch.Add(new SimpleStatement(
                    $"UPDATE {keyspace}.polls SET description = ?, updated_at = toTimestamp(now()) WHERE user_id = ? AND id = ?",
                    command.Description,
                    normalizedSystemId,
                    command.Id.Value));
            }

            if (command.HasTimeEnd)
            {
                updateBatch.Add(new SimpleStatement(
                    $"UPDATE {keyspace}.polls SET time_end = ?, updated_at = toTimestamp(now()) WHERE user_id = ? AND id = ?",
                    command.TimeEnd,
                    normalizedSystemId,
                    command.Id.Value));
            }

            if (command.Data is not null)
            {
                updateBatch.Add(new SimpleStatement(
                    $"UPDATE {keyspace}.polls SET data = ?, updated_at = toTimestamp(now()) WHERE user_id = ? AND id = ?",
                    command.Data.Value.GetRawText(),
                    normalizedSystemId,
                    command.Id.Value));
            }

            if (!updateBatch.IsEmpty)
            {
                await session.ExecuteAsync(updateBatch);
            }

            return true;
        }, _options, cancellationToken);
    }

    public async Task<bool> DeleteAsync(SystemId systemId, PollId pollId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            var exists = await ExistsAsync(session, keyspace, normalizedSystemId, pollId.Value);
            if (!exists)
                return false;

            var delete = new SimpleStatement(
                $"DELETE FROM {keyspace}.polls WHERE user_id = ? AND id = ?",
                normalizedSystemId,
                pollId.Value
            );
            await session.ExecuteAsync(delete);
            return true;
        }, _options, cancellationToken);
    }

    private static async Task<bool> ExistsAsync(ISession session, string keyspace, string normalizedSystemId, Guid pollGuid)
    {
        var query = new SimpleStatement(
            $"SELECT id FROM {keyspace}.polls WHERE user_id = ? AND id = ? LIMIT 1",
            normalizedSystemId,
            pollGuid
        );

        var rows = await session.ExecuteAsync(query);
        return rows.Any();
    }

    // The data blob's confirmed shape (see PollDataJson) keeps per-alter votes in the
    // top-level `responses` array as {"alter_id":<int>,...} entries; deleting an alter
    // filters those entries while leaving every other member untouched.
    public async Task RemoveAlterFromPollsAsync(SystemId systemId, AlterId alterId, CancellationToken cancellationToken = default)
    {
        var polls = await ListAsync(systemId, cancellationToken);

        foreach (var poll in polls)
        {
            if (!PollDataJson.TryRemoveAlterResponses(poll.Data, alterId, out var newData))
            {
                continue;
            }

            await UpdateAsync(systemId, new UpdatePollCommand(
                poll.Id,
                null,
                null,
                null,
                false,
                newData
            ), cancellationToken);
        }
    }

    private static PollReadModel ToReadModel(Row row)
        => new(
            new(row.GetValue<Guid>("id")),
            new(row.GetValue<string>("user_id")),
            row.GetValue<string>("title"),
            row.GetValue<string?>("description"),
            row.GetValue<short>("type").FromCode(PollType.Vote),
            JsonSerializer.Deserialize<JsonElement>(row.GetValue<string?>("data") ?? "{}"),
            row.GetValue<DateTime?>("time_end"),
            row.GetValue<DateTime>("inserted_at"),
            row.GetValue<DateTime>("updated_at")
        );
}
