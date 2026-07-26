using System.Diagnostics.Metrics;

namespace Interfold.Shared.Api.Helpers;

/// <summary>
/// Application-level named <see cref="Meter"/> with the counters and histograms used
/// for Phase N observability hardening.
/// Wire via <c>builder.Services.AddOpenTelemetry().WithMetrics(m => m.AddMeter("Interfold.Api"))</c>.
/// </summary>
public static class InterfoldMetrics
{
    public static readonly string MeterName = "Interfold.Api";

    private static readonly Meter Meter = new(MeterName, "1.0");

    /// <summary>
    /// Total command invocations.  Tags: <c>operation_id</c>, <c>outcome</c> (accepted|replay|rejected).
    /// </summary>
    public static readonly Counter<long> CommandsTotal = Meter.CreateCounter<long>(
        "octocon.commands.total",
        description: "Total command invocations tagged by operation_id and outcome.");

    /// <summary>
    /// Command conflicts.  Tags: <c>operation_id</c>, <c>conflict_code</c>.
    /// </summary>
    public static readonly Counter<long> ConflictsTotal = Meter.CreateCounter<long>(
        "octocon.commands.conflicts.total",
        description: "Command conflict counter tagged by operation_id and conflict_code.");

    /// <summary>
    /// Command handler wall-clock latency in milliseconds.  Tags: <c>operation_id</c>.
    /// </summary>
    public static readonly Histogram<double> CommandLatencyMs = Meter.CreateHistogram<double>(
        "octocon.commands.latency_ms",
        unit: "ms",
        description: "Command handler latency in milliseconds.");

    /// <summary>
    /// Guarded-visibility row filtering outcomes.  Tags: <c>entity_type</c>
    /// (<c>alter</c>|<c>tag</c>|<c>fronting</c>|<c>field</c>), <c>outcome</c>
    /// (<c>visible</c>|<c>filtered</c>).
    /// </summary>
    public static readonly Counter<long> GuardedFilteredTotal = Meter.CreateCounter<long>(
        "octocon.guarded.filtered.total",
        description: "Guarded rows tagged by entity_type and outcome (visible|filtered).");

    /// <summary>
    /// Guarded-visibility calls that returned no rows to the viewer (edge case worth
    /// surfacing separately from ordinary filtering).  Tags: <c>entity_type</c>.
    /// </summary>
    public static readonly Counter<long> GuardedNoResultsTotal = Meter.CreateCounter<long>(
        "octocon.guarded.no_results.total",
        description: "Guarded reads that returned zero visible rows, tagged by entity_type.");

    /// <summary>
    /// Guarded-visibility method wall-clock latency in milliseconds.  Tags:
    /// <c>entity_type</c>, <c>method</c> (<c>ListGuardedAsync</c>|<c>GetGuardedAsync</c>|
    /// <c>ListActiveGuardedAsync</c>).
    /// </summary>
    public static readonly Histogram<double> GuardedLatencyMs = Meter.CreateHistogram<double>(
        "octocon.guarded.latency_ms",
        unit: "ms",
        description: "Guarded read latency in milliseconds, tagged by entity_type and method.");

    /// <summary>
    /// Guarded-visibility errors bubbled out of the friendship-lookup / field-projection
    /// hot paths.  Tags: <c>entity_type</c>, <c>exception_type</c>.
    /// </summary>
    public static readonly Counter<long> GuardedErrorsTotal = Meter.CreateCounter<long>(
        "octocon.guarded.errors.total",
        description: "Guarded-path exceptions, tagged by entity_type and exception_type.");

    /// <summary>
    /// <c>PublicSystemsController</c> batch endpoint per-slice latency in milliseconds.
    /// Tags: <c>slice</c> (<c>aggregate</c>|<c>alter</c>|<c>tag</c>|<c>fronting</c>).
    /// </summary>
    public static readonly Histogram<double> GuardedBatchLatencyMs = Meter.CreateHistogram<double>(
        "octocon.guarded.batch.latency_ms",
        unit: "ms",
        description: "Batch public-systems endpoint latency in milliseconds, tagged by slice.");
}
