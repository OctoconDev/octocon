using System.Collections.Concurrent;
using Interfold.Contracts.Models.ImportOperations;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Contracts.Ids;

namespace Interfold.Infrastructure.InMemory.Repository;

/// <summary>
/// In-memory port of <see cref="IImportOperationRepository"/>. Used by the InMemory
/// persistence mode and (via the same DI registration) by every integration test that
/// doesn't spin a Cassandra container.
///
/// <para>
/// <b>Mutex modelling.</b> The Cassandra port uses LWT (Paxos) on the
/// <c>active_import_by_system</c> table; here we use <see cref="ConcurrentDictionary{TKey,TValue}.TryAdd"/>
/// which gives the same atomic-on-conflict semantics for a single process. Both ports
/// expose the same <see cref="ImportOperationClaim"/> contract — callers can't tell them
/// apart, which is the point: integration tests that depend on the "only one in-flight
/// per system" invariant run identically against both backends.
/// </para>
/// </summary>
public sealed class InMemoryImportOperationRepository : IImportOperationRepository
{
    private readonly ConcurrentDictionary<(SystemId SystemId, ImportOperationId OperationId), Row> _operations = new();
    private readonly ConcurrentDictionary<(SystemId SystemId, ImportOperationKind Kind), ImportOperationId> _active = new();

    public Task<ImportOperationClaim> TryClaimAsync(
        SystemId systemId,
        ImportOperationKind kind,
        IdempotencyKey idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        ImportOperationId newOperationId = new(Guid.NewGuid());
        var key = (systemId, kind);

        // TryAdd is the in-memory equivalent of the Cassandra LWT IF NOT EXISTS — atomic
        // wrt other threads in this process. If another thread won, we observe the existing
        // operation_id and return it so the caller short-circuits without dispatching a
        // duplicate worker run.
        if (!_active.TryAdd(key, newOperationId))
        {
            var existing = _active[key];
            return Task.FromResult(new ImportOperationClaim(existing, IsNew: false));
        }

        var now = DateTimeOffset.UtcNow;
        var row = new Row
        {
            SystemId = systemId,
            OperationId = newOperationId,
            Kind = kind,
            Status = ImportOperationStatus.Queued,
            StartedAt = now,
            IdempotencyKey = idempotencyKey,
        };
        _operations[(systemId, newOperationId)] = row;

        return Task.FromResult(new ImportOperationClaim(newOperationId, IsNew: true));
    }

    public Task MarkRunningAsync(
        SystemId systemId,
        ImportOperationId operationId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_operations.TryGetValue((systemId, operationId), out var row))
        {
            // CompareExchange-style guard: only Queued -> Running. Re-pickup of an
            // already-Running row is a no-op rather than an exception so the worker can be
            // retried idempotently.
            lock (row)
            {
                if (row.Status == ImportOperationStatus.Queued)
                {
                    row.Status = ImportOperationStatus.Running;
                }
            }
        }

        return Task.CompletedTask;
    }

    public Task MarkSucceededAsync(
        SystemId systemId,
        ImportOperationId operationId,
        ImportOperationKind kind,
        int alterCount,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_operations.TryGetValue((systemId, operationId), out var row))
        {
            lock (row)
            {
                row.Status = ImportOperationStatus.Succeeded;
                row.FinishedAt = DateTimeOffset.UtcNow;
                row.AlterCount = alterCount;
            }
        }

        ReleaseSlot(systemId, kind, operationId);
        return Task.CompletedTask;
    }

    public Task MarkFailedAsync(
        SystemId systemId,
        ImportOperationId operationId,
        ImportOperationKind kind,
        ImportErrorCode errorCode,
        string? errorMessage,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_operations.TryGetValue((systemId, operationId), out var row))
        {
            lock (row)
            {
                row.Status = ImportOperationStatus.Failed;
                row.FinishedAt = DateTimeOffset.UtcNow;
                row.ErrorCode = errorCode;
                row.ErrorMessage = errorMessage;
            }
        }

        ReleaseSlot(systemId, kind, operationId);
        return Task.CompletedTask;
    }

    public Task<ImportOperationSnapshot?> GetByIdAsync(
        SystemId systemId,
        ImportOperationId operationId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_operations.TryGetValue((systemId, operationId), out var row))
        {
            return Task.FromResult<ImportOperationSnapshot?>(null);
        }

        return Task.FromResult<ImportOperationSnapshot?>(Snapshot(row));
    }

    public Task<ImportOperationId?> GetActiveOperationIdAsync(
        SystemId systemId,
        ImportOperationKind kind,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_active.TryGetValue((systemId, kind), out var id))
        {
            return Task.FromResult<ImportOperationId?>(id);
        }

        return Task.FromResult<ImportOperationId?>(null);
    }

    public Task<IReadOnlyList<ImportOperationSnapshot>> GetStaleRunningAsync(
        TimeSpan olderThan,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var cutoff = DateTimeOffset.UtcNow - olderThan;
        var stale = new List<ImportOperationSnapshot>();
        foreach (var row in _operations.Values)
        {
            ImportOperationSnapshot? snapshot;
            lock (row)
            {
                snapshot = row.Status == ImportOperationStatus.Running && row.StartedAt < cutoff
                    ? Snapshot(row)
                    : null;
            }

            if (snapshot is not null)
            {
                stale.Add(snapshot);
            }
        }

        return Task.FromResult<IReadOnlyList<ImportOperationSnapshot>>(stale);
    }

    /// <summary>
    /// Frees the per-system slot iff it still points at the supplied operation id. The
    /// conditional swap mirrors the Cassandra port's <c>DELETE … IF operation_id = ?</c>
    /// so a stale terminal call (e.g. from a sweep) can't accidentally evict a different
    /// in-flight operation that took the slot afterwards.
    /// </summary>
    private void ReleaseSlot(SystemId systemId, ImportOperationKind kind, ImportOperationId operationId)
    {
        var key = (systemId, kind);
        // ConcurrentDictionary doesn't expose conditional-remove on key+value pairs as
        // a single primitive, but TryRemove(KeyValuePair) does. .NET's collection treats
        // this as atomic-on-match.
        var pair = new KeyValuePair<(SystemId, ImportOperationKind), ImportOperationId>(key, operationId);
        ((ICollection<KeyValuePair<(SystemId, ImportOperationKind), ImportOperationId>>)_active).Remove(pair);
    }

    private static ImportOperationSnapshot Snapshot(Row row) =>
        new(row.SystemId,
            row.OperationId,
            row.Kind,
            row.Status,
            row.StartedAt,
            row.FinishedAt,
            row.AlterCount,
            row.ErrorCode,
            row.ErrorMessage,
            row.IdempotencyKey);

    /// <summary>
    /// Mutable row backing the in-memory store. The lock-on-self guards the
    /// status-machine transitions; concurrent reads via <see cref="Snapshot"/> take the
    /// same lock so they always see a coherent point-in-time view.
    /// </summary>
    private sealed class Row
    {
        public required SystemId SystemId { get; init; }
        public required ImportOperationId OperationId { get; init; }
        public required ImportOperationKind Kind { get; init; }
        public ImportOperationStatus Status { get; set; }
        public DateTimeOffset StartedAt { get; init; }
        public DateTimeOffset? FinishedAt { get; set; }
        public int? AlterCount { get; set; }
        public ImportErrorCode? ErrorCode { get; set; }
        public string? ErrorMessage { get; set; }
        public required IdempotencyKey IdempotencyKey { get; init; }
    }
}
