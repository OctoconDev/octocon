using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Configuration;

namespace Interfold.AppHost.DevSeed;

/// <summary>
/// Concrete resource / parameter references the <see cref="DevSeedHostedService"/> needs at
/// runtime, resolved once during <see cref="DevSeedExtensions.AddDevSeedPipeline"/> and
/// injected via the DI singleton. Kept as a POCO — the hosted service uses it read-only.
/// </summary>
/// <remarks>
/// Every Aspire-managed secret is exposed as its <see cref="ParameterResource"/> so the
/// hosted service can call <see cref="ParameterResource.GetValueAsync"/> — that path always
/// returns the current effective value, whereas <see cref="IConfiguration"/> only sees
/// <c>GenerateParameterDefault</c> outputs on subsequent runs (after they're persisted to
/// user-secrets). Static / operator-set values (usernames, db name) still come from
/// configuration since they never regenerate.
/// </remarks>
internal sealed class DevSeedContext(
    DevSeedResource seedResource,
    IResourceWithEndpoints msgDbResource,
    IResourceWithEndpoints? cqlBackendResource,
    ParameterResource postgresInitPassword,
    ParameterResource postgresAppPassword,
    ParameterResource scyllaAppPassword,
    ParameterResource encryptionPepper,
    ParameterResource deepLinkSecret,
    ParameterResource postgresAdminPassword,
    ParameterResource scyllaAdminPassword,
    IConfiguration configuration)
{
    public DevSeedResource SeedResource { get; } = seedResource;
    public IResourceWithEndpoints MsgDbResource { get; } = msgDbResource;
    /// <summary>First CQL backend the AppHost mapped a host port to (Scylla node 0 in the
    /// default graph; Cassandra in the cassandra-only launch profile). Null when neither is
    /// enabled — the hosted service skips the CQL seed step.</summary>
    public IResourceWithEndpoints? CqlBackendResource { get; } = cqlBackendResource;
    public ParameterResource PostgresInitPassword { get; } = postgresInitPassword;
    public ParameterResource PostgresAppPassword { get; } = postgresAppPassword;
    public ParameterResource ScyllaAppPassword { get; } = scyllaAppPassword;
    public ParameterResource EncryptionPepper { get; } = encryptionPepper;
    public ParameterResource DeepLinkSecret { get; } = deepLinkSecret;
    public ParameterResource PostgresAdminPassword { get; } = postgresAdminPassword;
    public ParameterResource ScyllaAdminPassword { get; } = scyllaAdminPassword;
    public IConfiguration Configuration { get; } = configuration;
}
