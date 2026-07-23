using Cassandra;

namespace Interfold.Infrastructure.Scylla.Repository;

internal static class ScyllaExistsQueries
{
    public static async Task<bool> RowExistsAsync(ISession session, string keyspace, string table, string idColumn, string normalizedSystemId, object idValue)
    {
        var query = new SimpleStatement(
            $"SELECT {idColumn} FROM {keyspace}.{table} WHERE user_id = ? AND {idColumn} = ? LIMIT 1",
            normalizedSystemId,
            idValue
        );

        var rows = await session.ExecuteAsync(query);
        return rows.Any();
    }
}
