using Interfold.Bootstrapper.Cli;
using Interfold.Bootstrapper.Phases;
using Interfold.Shared.Contracts.Configuration;
using Interfold.Shared.Contracts.Enums;

namespace Interfold.Bootstrapper.UnitTests;

/// <summary>
/// Asserts the argv shape <see cref="BackupPhase"/> hands to docker compose for both the
/// pg_dump and nodetool snapshot pipelines. These tests don't exec anything — they just
/// pin the argv contract so a future refactor that swaps argument order or adds a flag
/// catches the change at unit speed rather than in the DinD integration suite.
/// </summary>
public sealed class BackupCommandBuildingTests
{
    [Test]
    public async Task PostgresDumpArgsUseAdminUserAndDatabase()
    {
        var args = BackupPhase.BuildPostgresDumpArgs(
            composeFile: "/srv/deploy/docker-compose.yaml",
            adminUser: "interfold_admin",
            database: "interfold");

        // Order is load-bearing: `compose` must be the first arg so docker dispatches to the
        // compose plugin; `-f` must come before the file path; `exec -T msg-db` must precede
        // pg_dump and its flags. Asserting the full sequence catches any silent re-ordering.
        await Assert.That(args).IsEquivalentTo(new[]
        {
            "compose", "-f", "/srv/deploy/docker-compose.yaml",
            "exec", "-T",
            "--env", Interfold.Bootstrapper.Util.DatabaseArchiveStreamer.PgPasswordEnvVar,
            "msg-db",
            "pg_dump",
            "-U", "interfold_admin",
            "-d", "interfold",
            "-Fc",
        });
    }

    [Test]
    public async Task PostgresDumpArgsDoNotContainPlaintextPassword()
    {
        // The password lands on the exec'd process via the PGPASSWORD env var (set elsewhere)
        // rather than the argv. Argv ends up in `ps` output and audit logs; passwords must
        // never appear there. We just assert no recognisable secret-shaped string.
        var args = BackupPhase.BuildPostgresDumpArgs(
            composeFile: "/srv/deploy/docker-compose.yaml",
            adminUser: "interfold_admin",
            database: "interfold");

        await Assert.That(args).DoesNotContain($"{Interfold.Bootstrapper.Util.DatabaseArchiveStreamer.PgPasswordEnvVar}=anything");
        // The literal "PGPASSWORD" appears as the env-var name (no `=value` suffix); that's
        // safe — only the value side leaks to ps.
        foreach (var arg in args)
        {
            await Assert.That(arg).DoesNotContain("password");
            await Assert.That(arg).DoesNotContain("secret");
        }
    }

    [Test]
    public async Task ScyllaSnapshotArgsTargetCorrectService()
    {
        var (snap, clear) = BackupPhase.BuildScyllaSnapshotArgs(
            composeFile: "/srv/deploy/docker-compose.yaml",
            service: "scylla",
            dataPath: ContainerMountPaths.ScyllaData,
            tag: "interfold-backup-20260301-120000");

        await Assert.That(snap).IsEquivalentTo(new[]
        {
            "compose", "-f", "/srv/deploy/docker-compose.yaml",
            "exec", "-T", "scylla",
            "nodetool", "snapshot", "-t", "interfold-backup-20260301-120000",
        });

        // The middle "resolve container id" step used to be part of this tuple; it now
        // lives at the call site via DockerCompose.PsAsync (see R2-C11). The archive
        // itself is still streamed out of the container by the host-side `docker cp`
        // step (see BuildContainerCpArgs) because the scylladb/scylla image ships no
        // `tar` binary — an in-container tar exits 127.
        await Assert.That(clear).IsEquivalentTo(new[]
        {
            "compose", "-f", "/srv/deploy/docker-compose.yaml",
            "exec", "-T", "scylla",
            "nodetool", "clearsnapshot", "-t", "interfold-backup-20260301-120000",
        });
    }

    [Test]
    public async Task BuildContainerCpArgsStreamsPathToStdout()
    {
        // `docker cp <id>:<path> -` is a daemon-level operation: it does not require any
        // binary inside the container. Passing `-` as the destination emits a raw tar
        // archive of the source path's contents on stdout, which BackupPhase then wraps
        // in a host-side GZipStream to produce the final .tar.gz on disk.
        var args = BackupPhase.BuildContainerCpArgs(
            containerId: "63fe606f0e95",
            dataPath: ContainerMountPaths.ScyllaData);

        await Assert.That(args).IsEquivalentTo(new[]
        {
            "cp", $"63fe606f0e95:{ContainerMountPaths.ScyllaData}", "-",
        });
    }

    [Test]
    public async Task BuildContainerCpArgsRejectsBlankInputs()
    {
        // Defensive: the caller resolves the container id from `docker compose ps -q` at
        // runtime — an empty result would silently produce `cp :<path> -` which docker
        // would interpret as a source-side host path, not a container path. Rejecting
        // early gives a clear error at the phase boundary.
        Assert.Throws<ArgumentException>(
            () => BackupPhase.BuildContainerCpArgs(containerId: "", dataPath: ContainerMountPaths.ScyllaData));
        Assert.Throws<ArgumentException>(
            () => BackupPhase.BuildContainerCpArgs(containerId: "abc", dataPath: "   "));
        await Task.CompletedTask;
    }

    [Test]
    public async Task ResolveScyllaSeedSingleModeUsesScyllaService()
    {
        // single-mode AppHost wires up the bare "scylla" service name; matches the AppHost
        // resource graph in InterfoldAppHost.Configure.
        var config = TestSupport.MakeConfig(DatabaseMode.Single);
        var (service, dataPath) = BackupPhase.ResolveScyllaSeed(config);

        await Assert.That(service).IsEqualTo("scylla");
        await Assert.That(dataPath).IsEqualTo(ContainerMountPaths.ScyllaData);
    }

    [Test]
    public async Task ResolveScyllaSeedMultiModeUsesNamSeed()
    {
        // multi-mode publishes 7 regional services; the seed (NAM) is the only one we
        // snapshot. Multi-DC operators that want all seven captured are documented as
        // out-of-scope for the bootstrapper itself.
        var config = TestSupport.MakeConfig(DatabaseMode.Multi);
        var (service, dataPath) = BackupPhase.ResolveScyllaSeed(config);

        await Assert.That(service).IsEqualTo("scylla-nam");
        await Assert.That(dataPath).IsEqualTo(ContainerMountPaths.ScyllaData);
    }

    [Test]
    public async Task ResolveScyllaSeedCassandraModeUsesCassandraService()
    {
        // cassandra-mode replaces Scylla entirely with a single Cassandra 5 node; the data
        // directory inside the official cassandra image is /var/lib/cassandra (not /var/lib/scylla).
        var config = TestSupport.MakeConfig(DatabaseMode.Cassandra);
        var (service, dataPath) = BackupPhase.ResolveScyllaSeed(config);

        await Assert.That(service).IsEqualTo("cassandra");
        await Assert.That(dataPath).IsEqualTo(ContainerMountPaths.CassandraData);
    }

    [Test]
    public async Task ResolveBackupRootPrefersCliOverride()
    {
        // Operator escape hatch wins over both config and the default. Path is normalised to
        // an absolute one because BackupPhase later wraps it in Directory.CreateDirectory +
        // EnumerateFiles, and both behave erratically with relative paths under a systemd
        // unit's unpredictable CWD.
        var options = TestSupport.MakeOptions(
            command: BootstrapCommand.Backup,
            backupDirOverride: "/srv/backups");

        var config = TestSupport.MakeConfig(tweak: c => c.Backup.Directory = "/var/never-seen");
        var resolved = BackupPhase.ResolveBackupRoot(options, config);

        // Path.GetFullPath normalises to the platform's separator style; just check it ends
        // with the expected suffix to keep this portable across Windows / Linux runners.
        await Assert.That(resolved.Replace('\\', '/'))
            .Contains("/srv/backups");
    }

    [Test]
    public async Task ResolveBackupRootFallsBackToConfig()
    {
        var options = TestSupport.MakeOptions(command: BootstrapCommand.Backup);

        var config = TestSupport.MakeConfig(tweak: c => c.Backup.Directory = "/var/backups/interfold");
        var resolved = BackupPhase.ResolveBackupRoot(options, config);

        await Assert.That(resolved.Replace('\\', '/'))
            .Contains("/var/backups/interfold");
    }

    [Test]
    public async Task ResolveBackupRootDefaultsToOutputDirSubfolder()
    {
        // Neither CLI nor config supplied → fall back to {outputDir}/backups. This is the
        // default path that 99% of operators will hit; the assertion confirms the join is
        // exactly "backups" (no typos, no leading slash).
        var outputDir = Path.GetFullPath("./deploy");
        var options = TestSupport.MakeOptions(
            command: BootstrapCommand.Backup,
            outputDir: outputDir);

        var config = TestSupport.MakeConfig();
        var resolved = BackupPhase.ResolveBackupRoot(options, config);

        await Assert.That(resolved).IsEqualTo(Path.Combine(outputDir, "backups"));
    }

    [Test]
    public async Task BuildArchiveFileNameProducesExpectedExtensions()
    {
        // Postgres uses pg_dump's custom binary format (.dump) restorable with pg_restore;
        // Scylla uses gzipped tar of the nodetool snapshot tree. The extensions are part of
        // the documented backup layout that operators rely on for ad-hoc tooling — pinning
        // them here so a refactor can't quietly switch to .pgdump or .tgz.
        await Assert.That(BackupPhase.BuildArchiveFileName(BackupDatabaseComponent.Postgres, "20260301-120000"))
            .IsEqualTo("20260301-120000.dump");
        await Assert.That(BackupPhase.BuildArchiveFileName(BackupDatabaseComponent.Scylla, "20260301-120000"))
            .IsEqualTo("20260301-120000.tar.gz");
    }

    [Test]
    public async Task ComponentParsing_RejectsUnknownAndDefaultsToAll()
    {
        // The CLI-boundary parse is what shields BuildArchiveFileName from meaningless
        // component values now that the helper takes the enum: unknown strings return
        // null (the phase surfaces its legacy --component error) and absent values keep
        // the historical "all" default.
        await Assert.That(BackupDatabaseComponentExtensions.TryParse("redis")).IsNull();
        await Assert.That(BackupDatabaseComponentExtensions.TryParse(null)).IsEqualTo(BackupDatabaseComponent.All);
        await Assert.That(BackupDatabaseComponentExtensions.TryParse("POSTGRES")).IsEqualTo(BackupDatabaseComponent.Postgres);
        await Assert.That(BackupDatabaseComponentExtensions.TryParse("scylla")).IsEqualTo(BackupDatabaseComponent.Scylla);
    }
}
