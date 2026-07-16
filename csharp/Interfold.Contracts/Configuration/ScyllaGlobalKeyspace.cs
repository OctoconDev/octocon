namespace Interfold.Contracts.Configuration;

/// <summary>
/// The singleton non-regional keyspace shared by every deployment topology (users
/// registry, friendships, friend requests, notification-token index, idempotency,
/// schema-migration ledger). Kept as a dedicated <see langword="const"/> so the wire
/// spelling has exactly one owner in C# and a rename picks up every CQL bind site.
///
/// This is deliberately NOT a value in <see cref="Interfold.Contracts.Enums.ScyllaKeyspace"/>
/// — that enum's invariant is "per-region keyspace identity"; the singleton is a
/// distinct concept and belongs on its own symbol.
/// </summary>
public static class ScyllaGlobalKeyspace
{
    public const string Name = "global";
}
