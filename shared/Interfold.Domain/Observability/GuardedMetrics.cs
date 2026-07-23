using System.Diagnostics.Metrics;

namespace Interfold.Domain.Observability;

// Mirror of the guarded-family instruments declared in Interfold.Api.Helpers.InterfoldMetrics.
// Domain and Infrastructure layers can't reach the Api project (would create a
// reference cycle), so the physical instrument definitions are duplicated here on
// the same "Interfold.Api" meter name. OpenTelemetry aggregates identically-named
// instrument samples by (meter, name, tags) at export time, so callers get a single
// time-series regardless of which side bumped it. Consolidates to a single source
// during feature-module-split Phase 1 (see docs/active/feature-module-split.md).
public static class GuardedMetrics
{
    private static readonly Meter Meter = new("Interfold.Api", "1.0");

    public static readonly Counter<long> FilteredTotal = Meter.CreateCounter<long>(
        "octocon.guarded.filtered.total",
        description: "Guarded rows tagged by entity_type and outcome (visible|filtered).");

    public static readonly Counter<long> NoResultsTotal = Meter.CreateCounter<long>(
        "octocon.guarded.no_results.total",
        description: "Guarded reads that returned zero visible rows, tagged by entity_type.");

    public static readonly Histogram<double> LatencyMs = Meter.CreateHistogram<double>(
        "octocon.guarded.latency_ms",
        unit: "ms",
        description: "Guarded read latency in milliseconds, tagged by entity_type and method.");

    public static readonly Counter<long> ErrorsTotal = Meter.CreateCounter<long>(
        "octocon.guarded.errors.total",
        description: "Guarded-path exceptions, tagged by entity_type and exception_type.");
}
