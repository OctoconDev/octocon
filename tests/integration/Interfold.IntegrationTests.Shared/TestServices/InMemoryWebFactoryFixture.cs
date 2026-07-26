using Interfold.Shared.Contracts;
using TUnit.Core.Interfaces;

namespace Interfold.IntegrationTests.Shared.TestServices;

/// <summary>
/// Standalone fixture (no Aspire, no Docker) that creates an in-memory WebApplicationFactory.
/// Always available — used as the baseline for all integration tests.
/// </summary>
public sealed class InMemoryWebFactoryFixture : IWebFactoryFixture, IAsyncInitializer
{
    public InterfoldWebApplicationFactory Factory { get; private set; } = null!;

    public Task InitializeAsync()
    {
        Factory = CreatePrivateFactory();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    // OCTOCON_SCYLLA_KEYSPACE defaults to "nam" via PersistenceConfiguration.ScyllaKeyspace,
    // so an explicit override here is redundant. Leave the factory at production defaults —
    // the private-factory shape must stay byte-identical to the session-shared one so tests
    // that opt into isolation exercise the same host wiring as the rest of the suite.
    public InterfoldWebApplicationFactory CreatePrivateFactory()
        => new(PersistenceMode.InMemory);
}
