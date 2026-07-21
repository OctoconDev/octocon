using Interfold.Contracts.Ids;
using Interfold.Domain.Abstractions.ImportJobs;

namespace Interfold.Domain.Abstractions;

/// <summary>
/// Performs a full data import from Simply Plural for a given system.
///
/// <para>
/// Returns an <see cref="ImportJobOutcome"/> directly — <see cref="SpImportJobRunner"/> is a
/// pass-through wrapper around this method, so the terminal-state contract is enforced here.
/// Implementations MUST NOT throw on graceful failures (auth failed, encryption not
/// initialised, upstream 4xx); those must land as <c>Success = false</c> with a populated
/// <see cref="ImportJobOutcome.ErrorCode"/> and <see cref="ImportJobOutcome.ErrorMessage"/>.
/// Throws are reserved for transport / programming errors and are classified by the worker
/// as <c>ImportErrorCode.Exception</c>.
/// </para>
/// </summary>
public interface ISimplyPluralImportService
{
    Task<ImportJobOutcome> ImportAsync(
        SystemId systemId,
        ImportToken spToken,
        RecoveryCode? encryptionKey,
        CancellationToken cancellationToken = default);

    bool? WaitForAvatars { get; set; }
}
