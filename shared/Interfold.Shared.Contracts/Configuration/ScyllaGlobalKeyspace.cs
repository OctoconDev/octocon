namespace Interfold.Shared.Contracts.Configuration;

/// <summary>Singleton non-regional keyspace (users registry, friendships, requests, tokens,
/// idempotency, migration ledger). Kept off <c>ScyllaKeyspace</c> because that enum
/// encodes per-region identity — the global keyspace is a distinct concept.</summary>
public static class ScyllaGlobalKeyspace
{
    public const string Name = "global";
}
