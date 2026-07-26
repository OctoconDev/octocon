using System.Diagnostics.Metrics;

namespace Interfold.Shared.Domain.Observability;

// Mirror of the guarded-family instruments also declared on the "Interfold.Api" meter by
// Interfold.Api.Helpers.InterfoldMetrics (in the Interfold.Shared.Api project). Domain and
// Infrastructure can't reference Shared.Api without forming a cycle, so the physical instrument
// declarations are duplicated here on the same meter name. OpenTelemetry aggregates by
// (meter, name, tags) at export, so a single time-series flows out regardless of which side
// bumps it.
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
