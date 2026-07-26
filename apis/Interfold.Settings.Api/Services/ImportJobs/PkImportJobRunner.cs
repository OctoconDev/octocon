using Interfold.Settings.Contracts.Models.ImportOperations;
using Interfold.Settings.Domain.Abstractions.ImportJobs;

namespace Interfold.Settings.Api.Services.ImportJobs;

/// <summary>
/// Stub runner that mirrors the placeholder state of the PluralKit import path. The real
/// importer hasn't landed yet (see <c>ImportPkCommandHandler</c>'s legacy comment), but
/// keeping the runner registered means:
///
/// <list type="bullet">
///   <item>The async-job machinery (queue, worker, operation row, LWT mutex) is identical for SP and PK — no second wiring pass required when the real importer lands.</item>
///   <item>The <c>pk_import_complete</c> WebSocket frame fires with <c>alter_count = 0</c> immediately on dispatch, which matches the existing handler's stub behaviour and keeps the contract self-consistent for any client that already listens.</item>
/// </list>
///
/// When the real importer lands, replace the body with a call to
/// <c>IPluralKitImportService.ImportAsync</c> following the same shape as
/// <see cref="SpImportJobRunner"/>.
/// </summary>
public sealed class PkImportJobRunner : IImportJobRunner
{
    public ImportOperationKind Kind => ImportOperationKind.PluralKit;

    public Task<ImportJobOutcome> RunAsync(ImportJobItem item, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new ImportJobOutcome(Success: false, AlterCount: 0,
            ErrorCode: ImportErrorCode.Unimplemented));
    }
}
