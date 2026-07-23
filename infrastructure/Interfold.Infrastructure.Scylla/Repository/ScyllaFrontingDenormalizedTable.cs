using System;
using Cassandra;
using Interfold.Shared.Contracts.Ids;

namespace Interfold.Infrastructure.Scylla.Repository;

internal static class ScyllaFrontingDenormalizedTable
{
    public static void AddFrontCloseStatements(
        BatchStatement batch,
        string keyspace,
        string systemId,
        Guid frontId,
        short alterId,
        DateTimeOffset startedAt,
        DateTimeOffset endedAt,
        string? comment,
        AlterId? primaryAlterId)
    {
        batch.Add(new SimpleStatement(
            $"UPDATE {keyspace}.fronts SET time_end = ?, updated_at = ? WHERE user_id = ? AND id = ? AND time_start = ?",
            endedAt,
            endedAt,
            systemId,
            frontId,
            startedAt));

        batch.Add(new SimpleStatement(
            $"DELETE FROM {keyspace}.current_fronts WHERE user_id = ? AND alter_id = ?",
            systemId,
            alterId));

        // Maintain fronts_by_alter (update time_end)
        batch.Add(new SimpleStatement(
            $"UPDATE {keyspace}.fronts_by_alter SET time_end = ?, updated_at = ? WHERE user_id = ? AND alter_id = ? AND id = ? AND time_start = ?",
            endedAt,
            endedAt,
            systemId,
            alterId,
            frontId,
            startedAt));

        // Insert into fronts_by_time (only on close)
        batch.Add(new SimpleStatement(
            $"INSERT INTO {keyspace}.fronts_by_time (user_id, time_start, time_end, id, alter_id, comment, inserted_at, updated_at) VALUES (?, ?, ?, ?, ?, ?, ?, ?)",
            systemId,
            startedAt,
            endedAt,
            frontId,
            alterId,
            comment,
            endedAt,
            endedAt));

        // Insert into fronts_by_end_time (only on close)
        batch.Add(new SimpleStatement(
            $"INSERT INTO {keyspace}.fronts_by_end_time (user_id, time_end, time_start, id, alter_id, comment, inserted_at, updated_at) VALUES (?, ?, ?, ?, ?, ?, ?, ?)",
            systemId,
            endedAt,
            startedAt,
            frontId,
            alterId,
            comment,
            endedAt,
            endedAt));

        if (primaryAlterId == new AlterId(alterId))
        {
            batch.Add(new SimpleStatement(
                $"UPDATE {keyspace}.users SET primary_front_alter = null, updated_at = toTimestamp(now()) WHERE id = ?",
                systemId));
        }
    }
}
