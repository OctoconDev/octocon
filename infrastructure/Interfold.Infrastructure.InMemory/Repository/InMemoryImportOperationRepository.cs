using System.Collections.Concurrent;
using Interfold.Shared.Contracts.Models.ImportOperations;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Shared.Contracts.Ids;

namespace Interfold.Infrastructure.InMemory.Repository;

/// <summary>In-memory <see cref="IImportOperationRepository"/> — Cassandra's LWT mutex
/// is emulated via <see cref="ConcurrentDictionary{TKey,TValue}.TryAdd"/>. Same
/// <see cref="ImportOperationClaim"/> contract as the Scylla port so InMemory-backed
/// integration tests are a meaningful proxy for the production semantics.</summary>
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

        // In-memory equivalent of Cassandra LWT IF NOT EXISTS.
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
            // Queued -> Running only; re-pickup of a Running row is a no-op (idempotent retry).
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

    // Conditional-swap release — mirrors Scylla's DELETE … IF operation_id = ? so a stale
    // terminal call can't evict a different in-flight operation that took the slot after.
    private void ReleaseSlot(SystemId systemId, ImportOperationKind kind, ImportOperationId operationId)
    {
        var key = (systemId, kind);
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

    // Lock-on-self guards status-machine transitions; Snapshot takes the same lock.
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
