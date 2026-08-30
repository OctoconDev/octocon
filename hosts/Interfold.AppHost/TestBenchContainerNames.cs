namespace Interfold.AppHost;

/// <summary>Fixed container names for test-bench mode (<c>WithContainerName</c> in
/// <see cref="InterfoldAppHost"/> and <c>docker rm -f</c> in the idle reaper).</summary>
public static class TestBenchContainerNames
{
    public const string Postgres = "interfold-test-bench-pg";
    public const string Scylla = "interfold-test-bench-scylla";
    public const string Cassandra = "interfold-test-bench-cassandra";

    public static readonly IReadOnlyList<string> All = [Postgres, Scylla, Cassandra];
}
