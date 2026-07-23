using System.Diagnostics;
using Interfold.Contracts.Ids;
using Microsoft.Extensions.Logging;

namespace Interfold.Domain.Observability;

// Centralised metric-emission + structured-log helpers for the guarded-visibility
// read paths. Keeps the tag shape identical across every repository (Alter, Tag,
// Fronting) and every backend (Scylla, InMemory) so downstream aggregators can
// group by entity_type without worrying about site-to-site tag drift.
public static class GuardedInstrumentation
{
    // For list-style calls: emits filtered/visible counters, latency histogram, and a
    // structured log line whose level depends on whether any rows were visible. Callers
    // pass their own entity_type ("alter" | "tag" | "fronting") and method name.
    public static void RecordList(
        ILogger logger,
        string entityType,
        string method,
        SystemId? viewerSystemId,
        string ownerSystemId,
        int totalCount,
        int visibleCount,
        double elapsedMs)
    {
        var filteredCount = totalCount - visibleCount;

        if (visibleCount > 0)
        {
            GuardedMetrics.FilteredTotal.Add(visibleCount, new TagList
            {
                { "entity_type", entityType },
                { "outcome", "visible" },
            });
        }

        if (filteredCount > 0)
        {
            GuardedMetrics.FilteredTotal.Add(filteredCount, new TagList
            {
                { "entity_type", entityType },
                { "outcome", "filtered" },
            });
        }

        GuardedMetrics.LatencyMs.Record(elapsedMs, new TagList
        {
            { "entity_type", entityType },
            { "method", method },
        });

        if (visibleCount == 0)
        {
            GuardedMetrics.NoResultsTotal.Add(1, new TagList { { "entity_type", entityType } });
            logger.LogWarning(
                "Guarded {EntityType} list returned no visible rows: caller={CallerId}, system={SystemId}, total={TotalCount}, duration_ms={DurationMs}",
                entityType, viewerSystemId?.Value, ownerSystemId, totalCount, elapsedMs);
        }
        else
        {
            logger.LogInformation(
                "Guarded {EntityType} list: caller={CallerId}, system={SystemId}, total={TotalCount}, visible={VisibleCount}, filtered={FilteredCount}, duration_ms={DurationMs}",
                entityType, viewerSystemId?.Value, ownerSystemId, totalCount, visibleCount, filteredCount, elapsedMs);
        }
    }

    // For single-item guarded gets: found records a visible bump, filtered records a
    // filtered bump, not-found bumps the no_results counter without touching filtered.
    public static void RecordGet(
        ILogger logger,
        string entityType,
        string method,
        SystemId? viewerSystemId,
        string ownerSystemId,
        string targetId,
        bool found,
        bool filtered,
        double elapsedMs)
    {
        if (found)
        {
            GuardedMetrics.FilteredTotal.Add(1, new TagList
            {
                { "entity_type", entityType },
                { "outcome", "visible" },
            });
        }

        if (filtered)
        {
            GuardedMetrics.FilteredTotal.Add(1, new TagList
            {
                { "entity_type", entityType },
                { "outcome", "filtered" },
            });
        }

        GuardedMetrics.LatencyMs.Record(elapsedMs, new TagList
        {
            { "entity_type", entityType },
            { "method", method },
        });

        if (!found)
        {
            GuardedMetrics.NoResultsTotal.Add(1, new TagList { { "entity_type", entityType } });
        }

        logger.LogInformation(
            "Guarded {EntityType} get: caller={CallerId}, system={SystemId}, target={TargetId}, found={Found}, filtered={Filtered}, duration_ms={DurationMs}",
            entityType, viewerSystemId?.Value, ownerSystemId, targetId, found, filtered, elapsedMs);
    }
}
