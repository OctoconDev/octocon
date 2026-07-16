using Interfold.Contracts.Models.ImportOperations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.ImportJobs;

namespace Interfold.Api.Services.ImportJobs;

/// <summary>
/// Bridges the generic <see cref="IImportJobRunner"/> contract to the concrete Simply
/// Plural importer (<see cref="ISimplyPluralImportService"/>). One per-process registration
/// (singleton). The background worker resolves this instance when it dequeues an item
/// with <see cref="ImportOperationKind.SimplyPlural"/>.
/// </summary>
public sealed class SpImportJobRunner : IImportJobRunner
{
    private readonly ISimplyPluralImportService _importService;

    public SpImportJobRunner(ISimplyPluralImportService importService)
    {
        _importService = importService;
    }

    public ImportOperationKind Kind => ImportOperationKind.SimplyPlural;

    public async Task<ImportJobOutcome> RunAsync(ImportJobItem item, CancellationToken cancellationToken = default)
    {
        // The service returns Success=false for graceful failures (auth, decryption, etc.)
        // and throws only on transport or programming errors — let those bubble so the
        // worker classifies them as exception-failed.
        var result = await _importService.ImportAsync(
            item.SystemId,
            item.Token,
            item.RecoveryCode,
            cancellationToken).ConfigureAwait(false);

        if (result.Success)
        {
            return new ImportJobOutcome(Success: true, AlterCount: result.AlterCount);
        }

        // The service supplies the stable machine code directly; sp_import_failed remains
        // the fallback so the client's terminal socket frame is unchanged when the service
        // omits one. The raw message lands in error_message for operators.
        return new ImportJobOutcome(
            Success: false,
            AlterCount: 0,
            ErrorCode: result.ErrorCode ?? ImportErrorCode.SpImportFailed,
            ErrorMessage: result.ErrorMessage);
    }
}
