using Interfold.Contracts.Models.ImportOperations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.ImportJobs;

namespace Interfold.Api.Services.ImportJobs;

/// <summary>
/// Bridges the generic <see cref="IImportJobRunner"/> contract to the concrete Simply
/// Plural importer (<see cref="ISimplyPluralImportService"/>). One per-process registration
/// (singleton). The background worker resolves this instance when it dequeues an item
/// with <see cref="ImportOperationKind.SimplyPlural"/>.
///
/// <para>
/// R7: <see cref="ISimplyPluralImportService.ImportAsync"/> now returns
/// <see cref="ImportJobOutcome"/> directly, so this runner is a pure pass-through. The
/// terminal-state contract (graceful failures must populate <see cref="ImportJobOutcome.ErrorCode"/>,
/// throws are classified as <c>exception</c>) is enforced by the service, not translated
/// here.
/// </para>
/// </summary>
public sealed class SpImportJobRunner : IImportJobRunner
{
    private readonly ISimplyPluralImportService _importService;

    public SpImportJobRunner(ISimplyPluralImportService importService)
    {
        _importService = importService;
    }

    public ImportOperationKind Kind => ImportOperationKind.SimplyPlural;

    public Task<ImportJobOutcome> RunAsync(ImportJobItem item, CancellationToken cancellationToken = default)
        => _importService.ImportAsync(item.SystemId, item.Token, item.RecoveryCode, cancellationToken);
}
