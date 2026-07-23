using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Domain.Abstractions.ImportJobs;

namespace Interfold.Shared.Domain.Abstractions;

/// <summary>Full SP data import for a system. MUST NOT throw on graceful failures
/// (auth, encryption, upstream 4xx) — return Success=false with ErrorCode/ErrorMessage.
/// Throws are transport/programming errors, classified by the worker as
/// <c>ImportErrorCode.Exception</c>.</summary>
public interface ISimplyPluralImportService
{
    Task<ImportJobOutcome> ImportAsync(
        SystemId systemId,
        ImportToken spToken,
        RecoveryCode? encryptionKey,
        CancellationToken cancellationToken = default);

    bool? WaitForAvatars { get; set; }
}
