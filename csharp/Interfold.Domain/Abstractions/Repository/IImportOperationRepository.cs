using Interfold.Contracts.Models.ImportOperations;
using Interfold.Contracts.Ids;

namespace Interfold.Domain.Abstractions.Repository;

/// <summary>
/// Persistence for the async-import job lifecycle. Backed by two Cassandra tables created
/// by <c>004_import_operations.templated.cql</c>: <c>import_operations</c> (append-only
/// history) and <c>active_import_by_system</c> (per-system LWT mutex).
///
/// <para>
/// <b>The load-bearing invariant.</b> At most one import operation per
/// <c>(system_id, kind)</c> may be in the <see cref="ImportOperationStatus.Queued"/> or
/// <see cref="ImportOperationStatus.Running"/> states at any time. Concurrent
/// <see cref="TryClaimAsync"/> calls collapse onto the existing in-flight operation
/// rather than starting a second one. This is what prevents the duplicate-SP-import
/// bug class from reappearing, regardless of how a duplicate dispatch arrived (browser
/// retry, Polly retry, multiple WebSocket frames, parallel sessions, etc.).
/// </para>
/// </summary>
public interface IImportOperationRepository
{
    /// <summary>
    /// Atomically claims the per-system import slot for <paramref name="kind"/>. If no
    /// import is currently in flight, inserts a new <see cref="ImportOperationStatus.Queued"/>
    /// row into <c>import_operations</c>, writes the active pointer into
    /// <c>active_import_by_system</c>, and returns a result with
    /// <see cref="ImportOperationClaim.IsNew"/> = true. If an import is already in flight
    /// (the active pointer exists), returns a result with
    /// <see cref="ImportOperationClaim.IsNew"/> = false carrying that operation's id, and
    /// the caller MUST NOT enqueue a second worker run.
    ///
    /// <para>
    /// The atomicity is provided by Cassandra LWT (Paxos round) on the
    /// <c>active_import_by_system</c> insert. On the rare LWT contention case (two
    /// dispatchers race for the same system) the loser observes the winning operation_id
    /// and returns it — no second row is created.
    /// </para>
    /// </summary>
    /// <param name="systemId">Octocon system id (regional prefix preserved).</param>
    /// <param name="kind">The third-party integration to claim the slot for.</param>
    /// <param name="idempotencyKey">The raw idempotency-key string the controller observed for this dispatch attempt. Pinned to the row for audit only — the per-system mutex is the real dedupe.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    Task<ImportOperationClaim> TryClaimAsync(
        SystemId systemId,
        ImportOperationKind kind,
        IdempotencyKey idempotencyKey,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Transitions a freshly-claimed operation row to <see cref="ImportOperationStatus.Running"/>.
    /// Called by the worker when it pulls the job off the queue. No-op if the row is not
    /// in <see cref="ImportOperationStatus.Queued"/> — guards against double-pickup if the
    /// channel ever delivers the same item twice.
    /// </summary>
    Task MarkRunningAsync(
        SystemId systemId,
        ImportOperationId operationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Terminal-success transition. Sets status to <see cref="ImportOperationStatus.Succeeded"/>,
    /// stamps <c>finished_at</c>, records the alter count, and releases the active
    /// slot (<c>DELETE FROM active_import_by_system IF operation_id = ?</c>) so the
    /// next click can dispatch.
    /// </summary>
    Task MarkSucceededAsync(
        SystemId systemId,
        ImportOperationId operationId,
        ImportOperationKind kind,
        int alterCount,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Terminal-failure transition. Sets status to <see cref="ImportOperationStatus.Failed"/>,
    /// stamps <c>finished_at</c>, records the <paramref name="errorCode"/> wire value +
    /// <paramref name="errorMessage"/>, and releases the active slot. The stable code lets
    /// future code branch on it; the message is for humans reading the operator log.
    /// </summary>
    Task MarkFailedAsync(
        SystemId systemId,
        ImportOperationId operationId,
        ImportOperationKind kind,
        ImportErrorCode errorCode,
        string? errorMessage,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Looks up a single operation row by id. Returns null if no row exists for the
    /// given system+id pair.
    /// </summary>
    Task<ImportOperationSnapshot?> GetByIdAsync(
        SystemId systemId,
        ImportOperationId operationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the operation_id of any import currently active for
    /// <c>(systemId, kind)</c>, or null if no import is in flight. Used by the dispatcher
    /// as a soft pre-check before <see cref="TryClaimAsync"/> when the controller wants
    /// to return a deterministic "already running" message without waiting on Paxos.
    /// (Phase 1 callers should always treat <see cref="TryClaimAsync"/> as the source of
    /// truth — this method's result is racy.)
    /// </summary>
    Task<ImportOperationId?> GetActiveOperationIdAsync(
        SystemId systemId,
        ImportOperationKind kind,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns every row currently in <see cref="ImportOperationStatus.Running"/> (across
    /// all systems for the current keyspace) whose <c>started_at</c> is older than
    /// <paramref name="olderThan"/>. Called by the background service's startup sweep so
    /// rows orphaned by a crash get marked failed instead of pinning the per-system slot
    /// indefinitely.
    ///
    /// <para>
    /// Note that this query is unbounded — it scans <c>import_operations</c> across all
    /// partitions. That's acceptable because the startup sweep runs once per process and
    /// the table grows at the rate of human-driven import clicks (low). If we ever need a
    /// faster sweep we can add a secondary index or a dedicated <c>orphaned_imports</c>
    /// table; v1 keeps it simple.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<ImportOperationSnapshot>> GetStaleRunningAsync(
        TimeSpan olderThan,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Outcome of an <see cref="IImportOperationRepository.TryClaimAsync"/> call. The caller
/// uses <see cref="IsNew"/> to decide whether to enqueue background work
/// (<c>IsNew == true</c>) or just return the existing <see cref="OperationId"/> to the
/// HTTP caller (<c>IsNew == false</c>).
/// </summary>
/// <param name="OperationId">Either the freshly-minted operation id or the id of the in-flight one.</param>
/// <param name="IsNew">True when the LWT successfully claimed the slot — the caller owns dispatching this work. False when the slot was already taken — the caller MUST NOT enqueue a second worker run.</param>
public readonly record struct ImportOperationClaim(ImportOperationId OperationId, bool IsNew);
