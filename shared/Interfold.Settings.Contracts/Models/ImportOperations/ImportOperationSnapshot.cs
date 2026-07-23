using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;

namespace Interfold.Contracts.Models.ImportOperations;

/// <summary>Read-side snapshot of a row in <c>import_operations</c>. Carries every column
/// so callers can decide (publish completion, mark failed, retry) without a repository
/// round-trip.</summary>
/// <param name="FinishedAt">Set only when Status is Succeeded / Failed.</param>
/// <param name="AlterCount">Set only when Status is Succeeded.</param>
/// <param name="ErrorCode">Null when Status ≠ Failed OR the persisted string no longer
/// parses (tolerant read via <see cref="EnumWire{TEnum}.TryParse"/>).</param>
/// <param name="IdempotencyKey">Audit-only. Per-system mutex on <c>active_import_by_system</c>
/// is the load-bearing dedupe mechanism.</param>
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
