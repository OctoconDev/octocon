namespace Interfold.IntegrationTests.Shared.TestServices.TestBench;

/// <summary>Connection information returned by <see cref="TestBenchCoordinator.EnsureRunningAsync"/>
/// so <c>BenchSharedDb</c> can attach to the running bench containers without going through
/// Aspire. Ports are the host-side bindings — everything binds to <c>127.0.0.1</c>.</summary>
public sealed record BenchConnectionInfo(
    string PostgresConnectionStringForInit,
    string PostgresConnectionStringForApp,
    int ScyllaPort,
    int CassandraPort,
    string LeaseFilePath);
