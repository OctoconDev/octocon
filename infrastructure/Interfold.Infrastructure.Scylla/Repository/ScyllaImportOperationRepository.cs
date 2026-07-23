using Cassandra;
using Interfold.Contracts.Configuration;
using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;
using Interfold.Contracts.Models.ImportOperations;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Interfold.Infrastructure.Scylla.Repository;

/// <summary>Cassandra / ScyllaDB port of <see cref="IImportOperationRepository"/>. Uses
/// LWT (Paxos) on <c>active_import_by_system</c> for the per-system claim mutex; terminal
/// transitions release the slot with <c>DELETE … IF operation_id = ?</c> so a stale
/// restart-sweep call can't evict a fresh in-flight operation. <see cref="ImportOperationId"/>
/// is Guid-backed on the wire and <c>timeuuid</c> on disk — cast at the bind sites.</summary>
public sealed class ScyllaImportOperationRepository : IImportOperationRepository
{
    private readonly IScyllaSessionProvider _sessionProvider;
    private readonly IScyllaKeyspaceResolver _keyspaceResolver;
    private readonly PersistenceConfiguration _options;
    private readonly IScyllaScopeResolver _scopeResolver;
    private readonly ILogger<ScyllaImportOperationRepository> _logger;

    public ScyllaImportOperationRepository(
        IScyllaSessionProvider sessionProvider,
        IScyllaKeyspaceResolver keyspaceResolver,
        IOptions<PersistenceConfiguration> options,
        IScyllaScopeResolver scopeResolver,
        ILogger<ScyllaImportOperationRepository> logger)
    {
        _sessionProvider = sessionProvider;
        _keyspaceResolver = keyspaceResolver;
        _options = options.Value;
        _scopeResolver = scopeResolver;
        _logger = logger;
    }

    public async Task<ImportOperationClaim> TryClaimAsync(
        SystemId systemId,
        ImportOperationKind kind,
        IdempotencyKey idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        return await _scopeResolver.ExecuteAsync<ImportOperationClaim>(systemId, async scope =>
        {
            var session = scope.Session;
            var keyspace = scope.Keyspace;
            var normalizedSystemId = scope.NormalizedSystemId;
            var now = DateTimeOffset.UtcNow;
            var newOperationId = TimeUuid.NewId();
            var kindWire = kind.ToWire();

            // Paxos INSERT IF NOT EXISTS — accepting a few hundred ms latency in exchange
            // for a strong per-system dedupe guarantee.
            var claim = new SimpleStatement(
                $"INSERT INTO {keyspace}.active_import_by_system " +
                "(system_id, kind, operation_id, started_at) VALUES (?, ?, ?, ?) IF NOT EXISTS",
                normalizedSystemId, kindWire, newOperationId, now.UtcDateTime);

            var claimResult = await session.ExecuteAsync(claim);
            var claimRow = claimResult.FirstOrDefault();
            // LWT result: one row with [applied] plus the existing column values on false.
            var applied = claimRow?.GetValue<bool>("[applied]") ?? false;

            if (!applied)
            {
                // Collapse duplicate dispatch onto the existing operation id.
                ImportOperationId existingId = new(claimRow!.GetValue<TimeUuid>("operation_id").ToGuid());
                _logger.LogInformation(
                    "[import-ops] Collapsed duplicate dispatch for system={SystemId} kind={Kind} onto operation_id={OperationId}.",
                    normalizedSystemId, kindWire, existingId);
                return new ImportOperationClaim(existingId, IsNew: false);
            }

            // History row is a plain INSERT — operation_id is a fresh TimeUuid so the
            // (system_id, operation_id) pair cannot collide.
            var historyInsert = new SimpleStatement(
                $"INSERT INTO {keyspace}.import_operations " +
                "(system_id, operation_id, kind, status, started_at, idempotency_key) " +
                "VALUES (?, ?, ?, ?, ?, ?)",
                normalizedSystemId, newOperationId, kindWire,
                ImportOperationStatus.Queued.ToWire(),
                now.UtcDateTime, idempotencyKey.Value);
            await session.ExecuteAsync(historyInsert);

            return new ImportOperationClaim(new(newOperationId.ToGuid()), IsNew: true);
        }, cancellationToken);
    }

    public async Task MarkRunningAsync(
        SystemId systemId,
        ImportOperationId operationId,
        CancellationToken cancellationToken = default)
    {
        await _scopeResolver.ExecuteAsync(systemId, async scope =>
        {
            var session = scope.Session;
            var keyspace = scope.Keyspace;
            var normalizedSystemId = scope.NormalizedSystemId;

            // Conditional update: re-pickup of a Running row is a no-op.
            var update = new SimpleStatement(
                $"UPDATE {keyspace}.import_operations SET status = ? " +
                "WHERE system_id = ? AND operation_id = ? IF status = ?",
                ImportOperationStatus.Running.ToWire(),
                normalizedSystemId, (TimeUuid)operationId.Value,
                ImportOperationStatus.Queued.ToWire());
            await session.ExecuteAsync(update);
            return true;
        }, cancellationToken);
    }

    public async Task MarkSucceededAsync(
        SystemId systemId,
        ImportOperationId operationId,
        ImportOperationKind kind,
        int alterCount,
        CancellationToken cancellationToken = default)
    {
        await _scopeResolver.ExecuteAsync(systemId, async scope =>
        {
            var session = scope.Session;
            var keyspace = scope.Keyspace;
            var normalizedSystemId = scope.NormalizedSystemId;
            var now = DateTimeOffset.UtcNow;

            var batch = new BatchStatement();
            batch.Add(new SimpleStatement(
                $"UPDATE {keyspace}.import_operations SET status = ?, finished_at = ?, alter_count = ? " +
                "WHERE system_id = ? AND operation_id = ?",
                ImportOperationStatus.Succeeded.ToWire(), now.UtcDateTime, alterCount,
                normalizedSystemId, (TimeUuid)operationId.Value));
            await session.ExecuteAsync(batch);

            await ReleaseSlot(session, keyspace, normalizedSystemId, kind.ToWire(), (TimeUuid)operationId.Value);
            return true;
        }, cancellationToken);
    }

    public async Task MarkFailedAsync(
        SystemId systemId,
        ImportOperationId operationId,
        ImportOperationKind kind,
        ImportErrorCode errorCode,
        string? errorMessage,
        CancellationToken cancellationToken = default)
    {
        await _scopeResolver.ExecuteAsync(systemId, async scope =>
        {
            var session = scope.Session;
            var keyspace = scope.Keyspace;
            var normalizedSystemId = scope.NormalizedSystemId;
            var now = DateTimeOffset.UtcNow;

            var update = new SimpleStatement(
                $"UPDATE {keyspace}.import_operations SET status = ?, finished_at = ?, error_code = ?, error_message = ? " +
                "WHERE system_id = ? AND operation_id = ?",
                ImportOperationStatus.Failed.ToWire(), now.UtcDateTime, errorCode.ToWire(), errorMessage,
                normalizedSystemId, (TimeUuid)operationId.Value);
            await session.ExecuteAsync(update);

            await ReleaseSlot(session, keyspace, normalizedSystemId, kind.ToWire(), (TimeUuid)operationId.Value);
            return true;
        }, cancellationToken);
    }

    public async Task<ImportOperationSnapshot?> GetByIdAsync(
        SystemId systemId,
        ImportOperationId operationId,
        CancellationToken cancellationToken = default)
    {
        return await _scopeResolver.ExecuteAsync<ImportOperationSnapshot?>(systemId, async scope =>
        {
            var session = scope.Session;
            var keyspace = scope.Keyspace;
            var normalizedSystemId = scope.NormalizedSystemId;

            var query = new SimpleStatement(
                $"SELECT system_id, operation_id, kind, status, started_at, finished_at, " +
                $"alter_count, error_code, error_message, idempotency_key " +
                $"FROM {keyspace}.import_operations WHERE system_id = ? AND operation_id = ? LIMIT 1",
                normalizedSystemId, (TimeUuid)operationId.Value);

            var rows = await session.ExecuteAsync(query);
            var row = rows.FirstOrDefault();
            return row is null ? null : MapRow(row);
        }, cancellationToken);
    }

    public async Task<ImportOperationId?> GetActiveOperationIdAsync(
        SystemId systemId,
        ImportOperationKind kind,
        CancellationToken cancellationToken = default)
    {
        return await _scopeResolver.ExecuteAsync<ImportOperationId?>(systemId, async scope =>
        {
            var session = scope.Session;
            var keyspace = scope.Keyspace;
            var normalizedSystemId = scope.NormalizedSystemId;

            var query = new SimpleStatement(
                $"SELECT operation_id FROM {keyspace}.active_import_by_system WHERE system_id = ? AND kind = ?",
                normalizedSystemId, kind.ToWire());

            var rows = await session.ExecuteAsync(query);
            var row = rows.FirstOrDefault();
            return row is null ? (ImportOperationId?)null : new ImportOperationId(row.GetValue<TimeUuid>("operation_id").ToGuid());
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<ImportOperationSnapshot>> GetStaleRunningAsync(
        TimeSpan olderThan,
        CancellationToken cancellationToken = default)
    {
        return await _scopeResolver.ExecuteGlobalAsync<IReadOnlyList<ImportOperationSnapshot>>(async scope =>
        {
            var session = scope.Session;
            var keyspace = _keyspaceResolver.DefaultKeyspace;
            var cutoff = DateTimeOffset.UtcNow - olderThan;

            // ALLOW FILTERING is fine — small table, startup-only query.
            var query = new SimpleStatement(
                $"SELECT system_id, operation_id, kind, status, started_at, finished_at, " +
                $"alter_count, error_code, error_message, idempotency_key " +
                $"FROM {keyspace}.import_operations " +
                "WHERE status = ? AND started_at < ? ALLOW FILTERING",
                ImportOperationStatus.Running.ToWire(), cutoff.UtcDateTime);

            var rows = await session.ExecuteAsync(query);
            var list = new List<ImportOperationSnapshot>();
            foreach (var row in rows)
            {
                list.Add(MapRow(row));
            }
            return (IReadOnlyList<ImportOperationSnapshot>)list;
        }, cancellationToken);
    }

    // IF operation_id = ? guards against evicting a fresh in-flight operation that took
    // the slot after our terminal transition; [applied]=false is expected for sweeps.
    private static async Task ReleaseSlot(
        ISession session,
        string keyspace,
        string normalizedSystemId,
        string kind,
        TimeUuid operationId)
    {
        var delete = new SimpleStatement(
            $"DELETE FROM {keyspace}.active_import_by_system " +
            "WHERE system_id = ? AND kind = ? IF operation_id = ?",
            normalizedSystemId, kind, operationId);
        await session.ExecuteAsync(delete);
    }

    private static ImportOperationSnapshot MapRow(Row row)
    {
        var statusText = row.GetValue<string>("status");
        var status = statusText.TryParseWire<ImportOperationStatus>(out var parsed)
            ? parsed
            : ImportOperationStatus.Queued;

        var kindText = row.GetValue<string>("kind");
        var kind = kindText.TryParseWire<ImportOperationKind>(out var parsedKind)
            ? parsedKind
            : throw new InvalidOperationException(
                $"[import-ops] Encountered unknown kind '{kindText}' in import_operations row. " +
                "Data schema drift — add the new value to ImportOperationKind before rolling this migration.");

        var startedAt = new DateTimeOffset(row.GetValue<DateTime>("started_at"), TimeSpan.Zero);
        var finishedAtRaw = row.GetValue<DateTime?>("finished_at");
        DateTimeOffset? finishedAt = finishedAtRaw.HasValue
            ? new DateTimeOffset(finishedAtRaw.Value, TimeSpan.Zero)
            : null;

        var errorCodeText = row.GetValue<string?>("error_code");
        ImportErrorCode? errorCode = errorCodeText.TryParseWire<ImportErrorCode>(out var parsedErrorCode)
            ? parsedErrorCode
            : null;

        return new ImportOperationSnapshot(
            new(row.GetValue<string>("system_id")),
            new(row.GetValue<TimeUuid>("operation_id").ToGuid()),
            kind,
            status,
            startedAt,
            finishedAt,
            row.GetValue<int?>("alter_count"),
            errorCode,
            row.GetValue<string>("error_message"),
            new(row.GetValue<string>("idempotency_key")));
    }
}
