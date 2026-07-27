using System.Security.Cryptography;
using Aspire.Hosting.ApplicationModel;
using Interfold.DatabaseBootstrap;
using Interfold.Shared.Contracts.Configuration;
using Interfold.Shared.Contracts.Enums;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Interfold.AppHost.DevSeed;

/// <summary>
/// Runs the in-process Postgres + Scylla seed after msg-db (and, when
/// <see cref="DevSeedContext.Includes"/> Scylla, the first Scylla node) reach
/// <see cref="KnownResourceStates.Running"/>, then flips
/// <see cref="DevSeedContext.SeedResource"/> to Running so any
/// <c>apiProject.WaitFor(seedResource)</c> unblocks. Publish mode never registers this
/// service (guard lives on the extension method), so the bootstrapper's
/// <c>DatabaseInitPhase</c> stays authoritative for self-hosted deployments.
/// </summary>
/// <remarks>
/// The seeders themselves are idempotent (see <c>PostgresSeeder.AppRoleAlreadyConfigured</c>
/// / <c>ScyllaSeeder.AdminRoleAlreadyConfigured</c>). Persistent-container reruns hit the
/// short-circuit and cost only a couple of probe round-trips. The JWT PEMs generated below
/// are wasted on repeat runs for the same reason — they're only ever written into
/// <c>internal.secrets</c> on the first pass.
/// </remarks>
internal sealed class DevSeedHostedService(
    DevSeedContext context,
    ResourceNotificationService notifications,
    ILogger<DevSeedHostedService> logger)
    : BackgroundService
{
    // The msg-db / scylla containers can be slow to reach Running on a fresh image pull.
    // Give the whole seed budget the same 10min ceiling the bootstrapper's Postgres readiness
    // probe uses; individual sub-steps have tighter caps inside DbInitHelper.
    private static readonly TimeSpan SeedBudget = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        budgetCts.CancelAfter(SeedBudget);
        var ct = budgetCts.Token;

        try
        {
            await notifications.PublishUpdateAsync(context.SeedResource,
                snap => snap with { State = new ResourceStateSnapshot(KnownResourceStates.Starting, "") });

            logger.LogInformation("Dev seed: waiting for msg-db to reach Running");
            await notifications.WaitForResourceAsync(context.MsgDbResource.Name, KnownResourceStates.Running, ct);

            var pgEndpoint = context.MsgDbResource.GetEndpoint("postgres");
            if (!pgEndpoint.IsAllocated)
            {
                throw new InvalidOperationException(
                    "msg-db reached Running but the 'postgres' endpoint is not allocated. " +
                    "This shouldn't happen — check the AppHost resource graph.");
            }

            // GenerateParameterDefault outputs land in user-secrets on Save (post-Build), so
            // on a first-run cold start Configuration[PostgresInitPassword] is still null.
            // GetValueAsync always returns the in-memory value the AppHost handed to the
            // msg-db container, first run or not.
            var initPassword = await context.PostgresInitPassword.GetValueAsync(ct)
                ?? throw new InvalidOperationException("Postgres init password parameter resolved to null.");
            var initConnectionString = BuildInitConnectionString(pgEndpoint.Host, pgEndpoint.Port, initPassword);
            logger.LogInformation("Dev seed: waiting for Postgres to accept connections at {Host}:{Port}",
                pgEndpoint.Host, pgEndpoint.Port);
            await InProcessSeedWaits.WaitForPostgresAsync(
                initConnectionString,
                // 3 consecutive successes matches the bootstrapper's readiness gate — filters out
                // the socket-open-but-still-crash-recovering window on cold TimescaleDB starts.
                new PostgresReadinessOptions(TimeSpan.FromMinutes(6), 3),
                ct);

            var cqlEndpoint = await ResolveCqlEndpointIfIncludedAsync(ct);

            var pgOptions = await BuildPostgresSeedOptionsAsync(cqlEndpoint, initPassword, ct);
            logger.LogInformation("Dev seed: bootstrapping Postgres roles + internal.secrets");
            var pgExecutor = new NpgsqlPostgresExecutor(initConnectionString);
            await PostgresSeeder.BootstrapAsync(pgExecutor, pgOptions, NoOpDatabaseInitLogger.Instance, ct);

            if (cqlEndpoint is not null)
            {
                logger.LogInformation("Dev seed: waiting for CQL backend at {Host}:{Port}",
                    cqlEndpoint.Host, cqlEndpoint.Port);
                await InProcessSeedWaits.WaitForScyllaAsync(cqlEndpoint.Host, cqlEndpoint.Port, ct);

                var scyllaOptions = await BuildScyllaSeedOptionsAsync(ct);
                logger.LogInformation("Dev seed: bootstrapping CQL roles");
                // ScyllaSeeder works against Cassandra too — same CREATE ROLE / ALTER ROLE surface,
                // and Cassandra ships with the same cassandra/cassandra bootstrap superuser.
                var scyllaExecutor = new DataStaxScyllaExecutor(cqlEndpoint.Host, cqlEndpoint.Port);
                await ScyllaSeeder.BootstrapAsync(scyllaExecutor, scyllaOptions, NoOpDatabaseInitLogger.Instance, ct);
            }

            await notifications.PublishUpdateAsync(context.SeedResource,
                snap => snap with { State = new ResourceStateSnapshot(KnownResourceStates.Running, "Success") });
            logger.LogInformation("Dev seed: complete");
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutting down mid-seed — don't spam the dashboard with a stack trace.
            await notifications.PublishUpdateAsync(context.SeedResource,
                snap => snap with { State = new ResourceStateSnapshot(KnownResourceStates.Exited, "Cancelled") });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Dev seed failed; the API resource's WaitFor(db-seed) will not unblock.");
            await notifications.PublishUpdateAsync(context.SeedResource,
                snap => snap with { State = new ResourceStateSnapshot(KnownResourceStates.FailedToStart, "Error") });
            throw;
        }
    }

    private async Task<EndpointReference?> ResolveCqlEndpointIfIncludedAsync(CancellationToken ct)
    {
        if (context.CqlBackendResource is null) return null;

        // Multi-node Scylla stages joins so later nodes may still be Joining when this fires;
        // the first node (which owns the host-mapped CQL endpoint) is the one we care about.
        await notifications.WaitForResourceAsync(context.CqlBackendResource.Name, KnownResourceStates.Running, ct);
        var endpoint = context.CqlBackendResource.GetEndpoint("cql");
        if (!endpoint.IsAllocated)
        {
            throw new InvalidOperationException(
                $"CQL resource '{context.CqlBackendResource.Name}' reached Running but the 'cql' endpoint " +
                "is not allocated. This shouldn't happen — check the AppHost resource graph.");
        }
        return endpoint;
    }

    private static string BuildInitConnectionString(string host, int port, string initPassword)
    {
        // db_init is the transient bootstrap superuser the msg-db container starts with. We
        // connect against the default `postgres` maintenance DB so PostgresSeeder can create
        // the app database with the admin role as owner.
        return $"Host={host};Port={port};Username={PostgresRoles.Init};Password={initPassword};Database=postgres;SSL Mode=Disable";
    }

    private async Task<PostgresSeedOptions> BuildPostgresSeedOptionsAsync(
        EndpointReference? cqlEndpoint, string initPassword, CancellationToken ct)
    {
        // Static / operator-set values (usernames, db name) come from configuration because
        // they never regenerate; passwords come from ParameterResource because
        // GenerateParameterDefault outputs aren't in IConfiguration on the first run.
        var appUser = context.Configuration[AppHostParameterKeys.PostgresUser]
            ?? throw new InvalidOperationException(
                $"Missing configuration for '{AppHostParameterKeys.PostgresUser}'.");
        var defaultDb = context.Configuration[AppHostParameterKeys.PostgresDb] ?? "interfold";
        var scyllaAppUser = context.Configuration[AppHostParameterKeys.ScyllaUser]
            ?? throw new InvalidOperationException(
                $"Missing configuration for '{AppHostParameterKeys.ScyllaUser}'.");

        var appPassword = await context.PostgresAppPassword.GetValueAsync(ct)
            ?? throw new InvalidOperationException("Postgres app password parameter resolved to null.");
        var scyllaAppPassword = await context.ScyllaAppPassword.GetValueAsync(ct)
            ?? throw new InvalidOperationException("Scylla app password parameter resolved to null.");
        var postgresAdminPassword = await context.PostgresAdminPassword.GetValueAsync(ct)
            ?? throw new InvalidOperationException("Postgres admin password parameter resolved to null.");
        var scyllaAdminPassword = await context.ScyllaAdminPassword.GetValueAsync(ct)
            ?? throw new InvalidOperationException("Scylla admin password parameter resolved to null.");
        var encryptionPepper = await context.EncryptionPepper.GetValueAsync(ct)
            ?? throw new InvalidOperationException("Encryption pepper parameter resolved to null.");
        var deepLinkSecret = await context.DeepLinkSecret.GetValueAsync(ct)
            ?? throw new InvalidOperationException("Deep-link secret parameter resolved to null.");

        // JWT PEMs regenerate on every AppHost start — the seeder short-circuits on subsequent
        // runs so only the first run actually writes them into internal.secrets. Wiping msg-db
        // volumes invalidates every issued JWT for the same reason a `rotate-secrets` does.
        var (rsaPem, es256Pem) = GenerateJwtPems();

        // Falls back to 9042 (the container-internal CQL port) when no CQL backend is enabled —
        // the API path that reads scylla:port never runs in that case.
        var scyllaPort = cqlEndpoint?.Port ?? 9042;

        return new PostgresSeedOptions(
            InitUser: PostgresRoles.Init,
            InitPassword: initPassword,
            AppUser: appUser,
            AppPassword: appPassword,
            // "{app}_admin" mirrors DatabaseInitPhase.BuildPostgresSeedOptions so dev + self-host
            // stay wire-compatible with any tooling that reads admin creds out of internal.secrets.
            AdminUser: $"{appUser}_admin",
            AdminPassword: postgresAdminPassword,
            DefaultDatabase: defaultDb,
            GoogleOAuthClientSecret: string.Empty,
            DiscordOAuthClientSecret: string.Empty,
            AppleOAuthClientSecret: string.Empty,
            EncryptionPepper: encryptionPepper,
            ScyllaContactPoints: cqlEndpoint?.Host ?? "127.0.0.1",
            ScyllaLocalDatacenter: ScyllaKeyspace.Nam.ToWire(),
            ScyllaAppUser: scyllaAppUser,
            ScyllaAppPassword: scyllaAppPassword,
            ScyllaPort: scyllaPort,
            ScyllaAdminUser: $"{scyllaAppUser}_admin",
            ScyllaAdminPassword: scyllaAdminPassword,
            JwtRsa256PrivateKeyPem: rsaPem,
            JwtEs256PrivateKeyPem: es256Pem,
            DeepLinkSecret: deepLinkSecret,
            LeafPfxPassword: string.Empty,
            // Never scramble in dev — persistent-container reruns still need db_init to
            // authenticate for the idempotency probe (bootstrapper only scrambles in prod).
            ScrambleInitUserPassword: false);
    }

    private async Task<ScyllaSeedOptions> BuildScyllaSeedOptionsAsync(CancellationToken ct)
    {
        var scyllaAppUser = context.Configuration[AppHostParameterKeys.ScyllaUser]
            ?? throw new InvalidOperationException(
                $"Missing configuration for '{AppHostParameterKeys.ScyllaUser}'.");
        var scyllaAppPassword = await context.ScyllaAppPassword.GetValueAsync(ct)
            ?? throw new InvalidOperationException("Scylla app password parameter resolved to null.");
        var scyllaAdminPassword = await context.ScyllaAdminPassword.GetValueAsync(ct)
            ?? throw new InvalidOperationException("Scylla admin password parameter resolved to null.");

        return new ScyllaSeedOptions(
            AppUser: scyllaAppUser,
            AppPassword: scyllaAppPassword,
            AdminUser: $"{scyllaAppUser}_admin",
            AdminPassword: scyllaAdminPassword,
            // Locking cassandra/cassandra matches self-host behaviour so a dev-mode operator
            // who copies compose flags across gets the same security profile.
            LockDefaultCassandra: true);
    }

    private static (string Rsa256Pem, string Es256Pem) GenerateJwtPems()
    {
        using var rsa = RSA.Create(2048);
        var rsaPem = rsa.ExportPkcs8PrivateKeyPem();

        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var esPem = ecdsa.ExportPkcs8PrivateKeyPem();
        return (rsaPem, esPem);
    }
}
