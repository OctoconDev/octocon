using Interfold.Contracts;
using TUnit.Core.Interfaces;

namespace Interfold.IntegrationTests.TestServices;

/// <summary>
/// Fixture chain that creates an <see cref="InterfoldWebApplicationFactory"/> backed by the
/// shared Cassandra 5 cluster managed by <see cref="SharedDbFixture"/>. TUnit resolves the
/// dependency automatically via <see cref="ClassDataSourceAttribute{T}"/>.
/// </summary>
/// <remarks>
/// Because this fixture references <see cref="SharedDbFixture"/>, the
/// <see cref="RequiredFixtures.DiscoverRequiredFixtures"/> hook flips
/// <see cref="RequiredFixtures.NeedCassandra"/> on for the session, which in turn flips the
/// <c>include-cassandra</c> Aspire parameter on. The <c>CassandraPort</c> property is
/// therefore always populated when this fixture's <see cref="InitializeAsync"/> runs.
/// </remarks>
public sealed class CassandraWebFactoryFixture : IWebFactoryFixture, IAsyncInitializer
{
    [ClassDataSource<SharedDbFixture>(Shared = SharedType.PerTestSession)]
    public required SharedDbFixture Aspire { get; init; }

    public InterfoldWebApplicationFactory Factory { get; private set; } = null!;

    public Task InitializeAsync()
    {
        // See ScyllaWebFactoryFixture for the rationale: SharedDbFixture captures per-backend
        // failures into CassandraInitException so that a wedged Cassandra container only
        // breaks Cassandra-backed tests, not the entire DB-bound test cohort.
        if (Aspire.CassandraInitException is { } cassandraFailure)
        {
            throw new InvalidOperationException(
                "CassandraWebFactoryFixture cannot start because the shared Cassandra backend " +
                "failed to initialise. See the inner exception for the underlying cause.",
                cassandraFailure);
        }

        Factory = CreatePrivateFactory();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    // Same wiring as InitializeAsync so a private factory exercises the identical Cassandra +
    // Postgres path. CassandraPort is guaranteed non-null by InitializeAsync's guard above —
    // this method is only reached after the fixture has passed that check.
    public InterfoldWebApplicationFactory CreatePrivateFactory()
        => new InterfoldWebApplicationFactory(PersistenceMode.ScyllaPostgres, "cassandra")
            .WithConfiguration("OCTOCON_POSTGRES_CONNECTION", Aspire.PostgresConnectionString)
            .WithConfiguration("OCTOCON_SCYLLA_PORT", Aspire.CassandraPort!.Value.ToString())
            .WithConfiguration("OCTOCON_SINGLE_SCYLLA_INSTANCE", "true")
            .WithConfiguration("OCTOCON_DB_RETRY_ATTEMPTS", "10")
            .WithConfiguration("OCTOCON_DB_RETRY_INITIAL_DELAY_MS", "500")
            .WithConfiguration("OCTOCON_DB_RETRY_MAX_DELAY_MS", "3000");
}
