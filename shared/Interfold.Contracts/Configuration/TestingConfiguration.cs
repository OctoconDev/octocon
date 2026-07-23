namespace Interfold.Contracts.Configuration;

/// <summary>
/// Testing environment configuration for integration test gating and connections.
/// Binds from environment variables with OCTOCON_RUN_* and OCTOCON_TEST_* prefixes.
/// </summary>
public sealed class TestingConfiguration
{
    public const string SectionName = "Octocon:Testing";

    /// <summary>
    /// When true, API integration tests (in-process API harness) will run.
    /// Env: OCTOCON_RUN_API_INTEGRATION
    /// </summary>
    public bool RunApiIntegration { get; init; }

    /// <summary>
    /// When true, live integration tests (real database connections) will run.
    /// Env: OCTOCON_RUN_LIVE_INTEGRATION
    /// </summary>
    public bool RunLiveIntegration { get; init; }

    /// <summary>
    /// Scylla contact points for live testing.
    /// Env: OCTOCON_TEST_SCYLLA_CONTACT_POINTS
    /// Default: '127.0.0.1'
    /// </summary>
    public string TestScyllaContactPoints { get; init; } = "127.0.0.1";

    /// <summary>
    /// Scylla username for live testing.
    /// Env: OCTOCON_TEST_SCYLLA_USERNAME
    /// Default: 'cassandra'
    /// </summary>
    public string TestScyllaUsername { get; init; } = "cassandra";

    /// <summary>
    /// Scylla password for live testing.
    /// Env: OCTOCON_TEST_SCYLLA_PASSWORD
    /// Default: 'cassandra'
    /// </summary>
    public string TestScyllaPassword { get; init; } = "cassandra";

    /// <summary>
    /// Region for live testing.
    /// Env: OCTOCON_TEST_REGION
    /// Default: 'nam'
    /// </summary>
    public string TestRegion { get; init; } = "nam";
}
