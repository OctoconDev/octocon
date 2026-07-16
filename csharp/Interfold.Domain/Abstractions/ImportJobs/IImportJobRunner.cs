using Interfold.Contracts.Models.ImportOperations;

namespace Interfold.Domain.Abstractions.ImportJobs;

/// <summary>
/// Per-kind importer used by the background worker. The worker resolves the right runner
/// by matching <see cref="Kind"/> against the dequeued <see cref="ImportJobItem.Kind"/>,
/// then hands the job to <see cref="RunAsync"/>.
///
/// <para>
/// Implementations live in the layer that owns the downstream client. For Simply Plural,
/// the runner is in <c>Interfold.Api/Services</c> because that's where
/// <c>ISimplyPluralImportService</c> lives. For PluralKit, the runner is a stub today,
/// matching the existing <c>ImportPkCommandHandler</c> behaviour.
/// </para>
/// </summary>
public interface IImportJobRunner
{
    /// <summary>
    /// The third-party integration this runner handles. The worker matches this against
    /// the dequeued <see cref="ImportJobItem.Kind"/>; add a new enum value + runner when
    /// integrating a new platform.
    /// </summary>
    ImportOperationKind Kind { get; }

    /// <summary>
    /// Executes the import. MUST NOT throw on graceful failures — those should return a
    /// result with <see cref="ImportJobOutcome.Success"/> = false and a populated
    /// <see cref="ImportJobOutcome.ErrorCode"/>. The worker catches any escaped exception
    /// and treats it as a thrown failure (<c>error_code = "exception"</c>).
    /// </summary>
    Task<ImportJobOutcome> RunAsync(ImportJobItem item, CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of running an import. Mirrors the worker's terminal-state vocabulary so the
/// worker can call <c>MarkSucceededAsync</c> or <c>MarkFailedAsync</c> without
/// re-classifying the outcome.
/// </summary>
/// <param name="Success">True for completion-with-data, false for any graceful failure.</param>
/// <param name="AlterCount">Number of alters the importer fetched (only meaningful on success).</param>
/// <param name="ErrorCode">Short stable machine code on failure. Null on success.</param>
/// <param name="ErrorMessage">Optional human-readable failure message. Null on success.</param>
public sealed record ImportJobOutcome(
    bool Success,
    int AlterCount,
    ImportErrorCode? ErrorCode = null,
    string? ErrorMessage = null);
