using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Interfold.AppHost.DevSeed;

/// <summary>
/// Wires the dev-mode seed pipeline into an <see cref="IDistributedApplicationBuilder"/>.
/// Only ever called from the RunMode branch of <c>InterfoldAppHost.Configure</c>; publish
/// mode never sees any of these registrations so the bootstrapper's own compose-exec seed
/// path stays authoritative for self-hosted deployments.
/// </summary>
internal static class DevSeedExtensions
{
    /// <summary>
    /// Registers the four generated dev-secret parameters, adds a marker
    /// <see cref="DevSeedResource"/> that consumers can <c>WaitFor(...)</c>, and hooks the
    /// <see cref="DevSeedHostedService"/> that drives the actual Postgres/Scylla seed.
    /// </summary>
    /// <param name="postgresInitPassword">Aspire-managed transient bootstrap password (used to
    /// authenticate against msg-db as db_init before <c>PostgresSeeder</c> mints the app roles).</param>
    /// <param name="postgresAppPassword">Aspire-managed application role password
    /// (<c>Parameters:postgres-password</c>) — passed through to <c>PostgresSeeder</c> so the
    /// app role created here matches what <c>ConfigureApiCommon</c> hands the API.</param>
    /// <param name="scyllaAppPassword">Aspire-managed CQL app role password
    /// (<c>Parameters:scylla-password</c>) — same story as <paramref name="postgresAppPassword"/>
    /// but for <c>ScyllaSeeder</c>.</param>
    /// <param name="cqlBackend">First CQL backend the AppHost mapped a host port to. Null when
    /// neither Scylla nor Cassandra are in the graph — the CQL seed step is skipped.</param>
    /// <returns>The <see cref="IResourceBuilder{DevSeedResource}"/> so the caller can pass it
    /// to <c>apiProject.WaitFor(...)</c>.</returns>
    public static IResourceBuilder<DevSeedResource> AddDevSeedPipeline(
        this IDistributedApplicationBuilder builder,
        IResourceBuilder<ContainerResource> msgDb,
        IResourceBuilder<ContainerResource>? cqlBackend,
        IResourceBuilder<ParameterResource> postgresInitPassword,
        IResourceBuilder<ParameterResource> postgresAppPassword,
        IResourceBuilder<ParameterResource> scyllaAppPassword)
    {
        // GenerateParameterDefault + persist:true writes the value into the AppHost's
        // dotnet user-secrets store on first Build(), so subsequent runs reuse it — matches
        // the postgres-init-password pattern already in the graph.
        var encryptionPepper = builder.AddParameter(
            DevSeedParameterNames.EncryptionPepper,
            new GenerateParameterDefault { MinLength = 32 },
            secret: true, persist: true);
        var deepLinkSecret = builder.AddParameter(
            DevSeedParameterNames.DeepLinkSecret,
            new GenerateParameterDefault { MinLength = 32 },
            secret: true, persist: true);
        var postgresAdminPassword = builder.AddParameter(
            DevSeedParameterNames.PostgresAdminPassword,
            new GenerateParameterDefault { MinLength = 32 },
            secret: true, persist: true);
        var scyllaAdminPassword = builder.AddParameter(
            DevSeedParameterNames.ScyllaAdminPassword,
            new GenerateParameterDefault { MinLength = 32 },
            secret: true, persist: true);

        var seedResourceBuilder = builder.AddResource(new DevSeedResource("db-seed"));

        var context = new DevSeedContext(
            seedResource: seedResourceBuilder.Resource,
            msgDbResource: msgDb.Resource,
            cqlBackendResource: cqlBackend?.Resource,
            postgresInitPassword: postgresInitPassword.Resource,
            postgresAppPassword: postgresAppPassword.Resource,
            scyllaAppPassword: scyllaAppPassword.Resource,
            encryptionPepper: encryptionPepper.Resource,
            deepLinkSecret: deepLinkSecret.Resource,
            postgresAdminPassword: postgresAdminPassword.Resource,
            scyllaAdminPassword: scyllaAdminPassword.Resource,
            configuration: builder.Configuration);

        builder.Services.AddSingleton(context);
        builder.Services.AddHostedService<DevSeedHostedService>();
        // Aspire's OrchestratorHostService.StartAsync blocks until every WaitFor-gated
        // resource unblocks — with the default sequential IHost startup, our
        // BackgroundService.StartAsync never runs, db-seed never flips to Running, and
        // interfold-api stays Waiting forever. Opting into concurrent startup lets our
        // hosted service fire in parallel and publish the state the orchestrator is waiting on.
        builder.Services.Configure<HostOptions>(o => o.ServicesStartConcurrently = true);

        return seedResourceBuilder;
    }
}
