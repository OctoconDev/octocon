using Interfold.Settings.Contracts.Ids;
using Interfold.Settings.Contracts.Models.ImportOperations;
using Interfold.Shared.Contracts.Ids;

namespace Interfold.Settings.Domain.Abstractions.Repository;

/// <summary>Async-import job persistence. Backed by <c>import_operations</c> (append-only
/// history) + <c>active_import_by_system</c> (per-system LWT mutex). Invariant: at most
/// one operation per (system, kind) may be Queued or Running at a time — prevents the
/// duplicate-SP-import bug regardless of how a duplicate dispatch arrived.</summary>
public interface IImportOperationRepository
{
    /// <summary>Atomically claims the per-system import slot via LWT on
    /// <c>active_import_by_system</c>. If no slot exists, inserts a new Queued row and
    /// returns <see cref="ImportOperationClaim.IsNew"/> = true; otherwise returns the
    /// existing operation id with IsNew = false and the caller MUST NOT enqueue another
    /// worker run.</summary>
    Task<ImportOperationClaim> TryClaimAsync(
        SystemId systemId,
        ImportOperationKind kind,
        IdempotencyKey idempotencyKey,
        CancellationToken cancellationToken = default);

    /// <summary>Queued → Running. No-op when the row isn't Queued (guards against
    /// double-pickup on channel replay).</summary>
    Task MarkRunningAsync(
        SystemId systemId,
        ImportOperationId operationId,
        CancellationToken cancellationToken = default);

    /// <summary>Terminal-success transition: sets Succeeded + finished_at + alter_count and
    /// releases the active slot so the next dispatch can proceed.</summary>
    Task MarkSucceededAsync(
        SystemId systemId,
        ImportOperationId operationId,
        ImportOperationKind kind,
        int alterCount,
        CancellationToken cancellationToken = default);

    /// <summary>Terminal-failure transition: sets Failed + finished_at + error and releases
    /// the active slot. Wire error_code is stable for branching; message is human-only.</summary>
    Task MarkFailedAsync(
        SystemId systemId,
        ImportOperationId operationId,
        ImportOperationKind kind,
        ImportErrorCode errorCode,
        string? errorMessage,
        CancellationToken cancellationToken = default);

    Task<ImportOperationSnapshot?> GetByIdAsync(
        SystemId systemId,
        ImportOperationId operationId,
        CancellationToken cancellationToken = default);

    /// <summary>Racy soft pre-check for a deterministic "already running" reply without
    /// waiting on Paxos. TryClaimAsync stays the source of truth.</summary>
    Task<ImportOperationId?> GetActiveOperationIdAsync(
        SystemId systemId,
        ImportOperationKind kind,
        CancellationToken cancellationToken = default);

    /// <summary>Startup-sweep query: every Running row older than <paramref name="olderThan"/>
    /// so crash-orphaned rows can be marked failed instead of pinning the per-system slot.
    /// Unbounded partition scan — acceptable at human-driven import volume.</summary>
    Task<IReadOnlyList<ImportOperationSnapshot>> GetStaleRunningAsync(
        TimeSpan olderThan,
        CancellationToken cancellationToken = default);
}

/// <summary>Outcome of <see cref="IImportOperationRepository.TryClaimAsync"/>. IsNew =
/// true → caller owns dispatching this work; false → slot already taken, do not enqueue.</summary>
public readonly record struct ImportOperationClaim(ImportOperationId OperationId, bool IsNew);
