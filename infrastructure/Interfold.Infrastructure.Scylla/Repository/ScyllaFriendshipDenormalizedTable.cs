using Cassandra;
using Interfold.Shared.Contracts.Configuration;

namespace Interfold.Infrastructure.Scylla.Repository;

internal static class ScyllaFriendshipDenormalizedTable
{
    public static void AddInsertStatements(BatchStatement batch, string systemId, string otherSystemId, short friendLevel)
    {
        batch.Add(new SimpleStatement(
            $"INSERT INTO {ScyllaGlobalKeyspace.Name}.friendships (user_id, friend_id, level, since, inserted_at, updated_at) VALUES (?, ?, ?, toTimestamp(now()), toTimestamp(now()), toTimestamp(now()))",
            systemId, otherSystemId, friendLevel));
        batch.Add(new SimpleStatement(
            $"INSERT INTO {ScyllaGlobalKeyspace.Name}.friendships (user_id, friend_id, level, since, inserted_at, updated_at) VALUES (?, ?, ?, toTimestamp(now()), toTimestamp(now()), toTimestamp(now()))",
            otherSystemId, systemId, friendLevel));
        // Maintain friendships_by_friend_id denormalized table
        batch.Add(new SimpleStatement(
            $"INSERT INTO {ScyllaGlobalKeyspace.Name}.friendships_by_friend_id (friend_id, user_id, level, since, inserted_at, updated_at) VALUES (?, ?, ?, toTimestamp(now()), toTimestamp(now()), toTimestamp(now()))",
            otherSystemId, systemId, friendLevel));
        batch.Add(new SimpleStatement(
            $"INSERT INTO {ScyllaGlobalKeyspace.Name}.friendships_by_friend_id (friend_id, user_id, level, since, inserted_at, updated_at) VALUES (?, ?, ?, toTimestamp(now()), toTimestamp(now()), toTimestamp(now()))",
            systemId, otherSystemId, friendLevel));
    }

    public static void AddDeleteStatements(BatchStatement batch, string systemId, string otherSystemId)
    {
        batch.Add(new SimpleStatement(
            $"DELETE FROM {ScyllaGlobalKeyspace.Name}.friendships WHERE user_id = ? AND friend_id = ?",
            systemId, otherSystemId));
        batch.Add(new SimpleStatement(
            $"DELETE FROM {ScyllaGlobalKeyspace.Name}.friendships WHERE user_id = ? AND friend_id = ?",
            otherSystemId, systemId));
        batch.Add(new SimpleStatement(
            $"DELETE FROM {ScyllaGlobalKeyspace.Name}.friendships_by_friend_id WHERE friend_id = ? AND user_id = ?",
            otherSystemId, systemId));
        batch.Add(new SimpleStatement(
            $"DELETE FROM {ScyllaGlobalKeyspace.Name}.friendships_by_friend_id WHERE friend_id = ? AND user_id = ?",
            systemId, otherSystemId));
    }
}
