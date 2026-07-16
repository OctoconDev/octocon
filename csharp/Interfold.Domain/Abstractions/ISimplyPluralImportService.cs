using Interfold.Contracts.Ids;
using Interfold.Contracts.Models.ImportOperations;

namespace Interfold.Domain.Abstractions;

/// <summary>
/// Performs a full data import from Simply Plural for a given system.
/// </summary>
public interface ISimplyPluralImportService
{
    Task<SpImportResult> ImportAsync(
        SystemId systemId,
        ImportToken spToken,
        RecoveryCode? encryptionKey,
        CancellationToken cancellationToken = default);

    bool? WaitForAvatars { get; set; }
}

/// <summary>
/// Import outcome. <see cref="ErrorCode"/> is the stable machine code the worker persists
/// to <c>import_operations.error_code</c>; <see cref="ErrorMessage"/> is the human-readable
/// detail for operator logs. Both null on success.
/// </summary>
public sealed record SpImportResult(bool Success, int AlterCount, ImportErrorCode? ErrorCode = null, string? ErrorMessage = null);
