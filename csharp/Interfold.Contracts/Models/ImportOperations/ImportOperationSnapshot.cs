using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;

namespace Interfold.Contracts.Models.ImportOperations;

/// <summary>
/// Read-side snapshot of a row in the <c>import_operations</c> table. Returned by
/// <c>IImportOperationRepository.GetByIdAsync</c> and the startup-sweep query. Carries
/// every column so the caller can decide what to do (publish a completion event, mark
/// failed, retry, etc.) without going back to the repository.
/// </summary>
/// <param name="SystemId">Octocon system id (regional prefix included, e.g. "nam:abc123").</param>
/// <param name="OperationId">TimeUuid surrogate key — also used as the public correlation handle returned to the HTTP caller.</param>
/// <param name="Kind">Third-party integration this row was raised for.</param>
/// <param name="Status">Current lifecycle state.</param>
/// <param name="StartedAt">When the dispatcher claimed the slot. Always present.</param>
/// <param name="FinishedAt">When the worker transitioned the row to <see cref="ImportOperationStatus.Succeeded"/> or <see cref="ImportOperationStatus.Failed"/>. Null while <c>Queued</c> or <c>Running</c>.</param>
/// <param name="AlterCount">Number of alters the importer reported on success. Null for non-succeeded rows.</param>
/// <param name="ErrorCode">Stable machine code on failure. Null otherwise — including for legacy rows whose persisted string no longer parses (tolerant read via <see cref="EnumWire{TEnum}.TryParse"/>); pair with <paramref name="ErrorMessage"/> for the human detail.</param>
/// <param name="ErrorMessage">Optional human-readable failure message. Null otherwise.</param>
/// <param name="IdempotencyKey">The raw idempotency-key string the dispatching controller observed (any client-supplied shape — UUID, base64, Guid.ToString("N"), etc.). Pinned here for audit / debugging only — the per-system mutex (active_import_by_system) is the load-bearing dedupe mechanism.</param>
public sealed record ImportOperationSnapshot(
    SystemId SystemId,
    ImportOperationId OperationId,
    ImportOperationKind Kind,
    ImportOperationStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    int? AlterCount,
    ImportErrorCode? ErrorCode,
    string? ErrorMessage,
    IdempotencyKey IdempotencyKey);
