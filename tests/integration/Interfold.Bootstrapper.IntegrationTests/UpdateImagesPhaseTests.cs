using Interfold.Bootstrapper.IntegrationTests.Attributes;
using Interfold.Bootstrapper.IntegrationTests.Fixtures;

namespace Interfold.Bootstrapper.IntegrationTests;

/// <summary>
/// Integration tests for the <c>update-images</c> subcommand against a real running compose
/// stack inside the shared Ubuntu DinD fixture. Each test runs a full <c>bootstrap</c> first,
/// then exercises the update pipeline (pull + optional recreate + health check + retention)
/// and asserts on the on-disk backup archives, compose state, and exit codes.
/// </summary>
/// <remarks>
/// <para>
/// These tests exercise the happy path only: the DinD fixture pre-loads
/// <c>interfold-api:test</c> and <c>scylladb/scylla:2026.1</c> as fixed image IDs, so a
/// <c>docker compose pull</c> in a network-isolated DinD against the registry-backed
/// services (<c>msg-db</c> = TimescaleDB, <c>scylla</c> = ScyllaDB) is a no-op at the
/// digest level. That's exactly the "no changes" branch we want to pin — the fail
/// branches (image bump, health check failure, auto-restore recovery) require registry
/// mutation and are intentionally deferred to a follow-up PR that stands up an in-DinD
/// registry container.
/// </para>
/// <para>
/// The "no changes" case is still worth wiring end-to-end because it exercises the full
/// argv/parse pipeline: config load, compose discovery, backup invocation, pull, digest
/// diff, and the early-exit branch — all against a real running stack, not a mock.
/// </para>
/// <para>
/// Every <c>update-images</c> invocation here scopes to <c>--service msg-db --service
/// scylla</c> because <c>interfold-api:test</c> is a locally-loaded tag with no
/// matching registry manifest; a bare <c>docker compose pull</c> would exit non-zero
/// with <c>"pull access denied for interfold-api"</c> before the phase's digest diff
/// even ran. Skipping the api service on the pull side loses no coverage of the
/// pipeline mechanics under test (the "no-op", pre-update backup, and
/// <c>--skip-pre-update-backup</c> branches are all orthogonal to which services get
/// pulled). The general "operator uses a locally-built API image" case is a separate
/// product concern — see the pull-skip discussion in
/// <see cref="Phases.PublishPhase.StampCassandraPullPolicyNever"/> for the pattern
/// that would extend it to the api service too.
/// </para>
/// </remarks>
[RequiresDocker]
[ClassDataSource<UbuntuDinDFixture>(Shared = SharedType.PerTestSession)]
[Explicit]
public class UpdateImagesPhaseTests(UbuntuDinDFixture dinD)
{

    [After(Test)]
    public Task DumpOnFailure(TestContext ctx) => DinDHookHelpers.DumpOnFailureAsync(dinD, ctx);

    [Test]
    public async Task UpdateWithNoChangedImagesIsNoOp()
    {
        // Full bootstrap first: brings up postgres + scylla + api against the fixture's
        // pre-loaded images. Then run `update-images` — the pull is a no-op (nothing in the
        // fixture points at a mutable registry tag), so the digest diff must be empty and
        // the phase short-circuits without touching the running containers.
        var (scratch, _) = await dinD.BootstrapAsync(nameof(UpdateWithNoChangedImagesIsNoOp), TestConfigPaths.DefaultConfig);

        // Capture the api container ID before the update so we can assert it wasn't
        // recreated (recreate is what makes update-images observable when digests change;
        // a no-op update must NOT touch the running container).
        var beforeId = await dinD.ExecAsync(["sh", "-c",
            $"docker compose -f {scratch.OutputDir}/docker-compose.yaml ps -q interfold-api"]);
        await Assert.That(beforeId.ExitCode).IsEqualTo(0L);
        var apiIdBefore = beforeId.Stdout.Trim();
        await Assert.That(apiIdBefore.Length).IsGreaterThan(0)
            .Because("bootstrap should have produced a running interfold-api container");

        var update = await dinD.RunOnScratchAsync(scratch, nameof(UpdateWithNoChangedImagesIsNoOp), "update-images",
            "--service", "msg-db", "--service", "scylla");
        await Assert.That(update.ExitCode).IsEqualTo(0).Because($"update-images failed: {update.Stderr}");

        var combined = update.Stdout + update.Stderr;
        await Assert.That(combined).Contains("no-op")
            .Because("with no image changes the phase must log a no-op branch and skip recreate");

        // Container ID unchanged — proves nothing was recreated. If update-images had
        // called `up -d` on a service whose digest hadn't changed, compose would still
        // leave the container ID stable, but the fact that we see the log line above
        // AND the ID is stable pins the branch as well as the observable outcome.
        var afterId = await dinD.ExecAsync(["sh", "-c",
            $"docker compose -f {scratch.OutputDir}/docker-compose.yaml ps -q interfold-api"]);
        await Assert.That(afterId.Stdout.Trim()).IsEqualTo(apiIdBefore)
            .Because("no-op update must not recreate the api container");
    }

    [Test]
    public async Task UpdatePerformsPreUpdateBackup()
    {
        // The backup step is always taken unless --skip-pre-update-backup is passed.
        // Even on the no-op path we should see fresh archives in {outputDir}/backups/
        // because the backup fires BEFORE the digest diff (so a pull that turns out to
        // be a no-op still leaves a recovery snapshot on disk).
        var (scratch, _) = await dinD.BootstrapAsync(nameof(UpdatePerformsPreUpdateBackup), TestConfigPaths.DefaultConfig);

        // Baseline: no backups exist yet.
        var pgBefore = await dinD.CountFilesAsync(scratch, "backups/postgres/*.dump");
        await Assert.That(pgBefore).IsEqualTo(0)
            .Because("baseline: bootstrap should not have taken any backups yet");

        var update = await dinD.RunOnScratchAsync(scratch, nameof(UpdatePerformsPreUpdateBackup), "update-images",
            "--service", "msg-db", "--service", "scylla");
        await Assert.That(update.ExitCode).IsEqualTo(0).Because($"update-images failed: {update.Stderr}");

        // Both components must have received a fresh archive from the pre-update backup step.
        var pgAfter = await dinD.CountFilesAsync(scratch, "backups/postgres/*.dump");
        await Assert.That(pgAfter).IsEqualTo(1)
            .Because("update-images must take a pre-update postgres backup");

        var scyllaAfter = await dinD.CountFilesAsync(scratch, "backups/scylla/*.tar.gz");
        await Assert.That(scyllaAfter).IsEqualTo(1)
            .Because("update-images must take a pre-update scylla backup");
    }

    [Test]
    public async Task UpdateWithSkipPreUpdateBackupDoesNotWriteArchives()
    {
        // Escape-hatch flag: --skip-pre-update-backup MUST prevent any backup being taken
        // (the operator has just taken a manual one and doesn't want the duplicate).
        // Pinned so a future refactor that flips the default behaviour gets caught.
        var (scratch, _) = await dinD.BootstrapAsync(nameof(UpdateWithSkipPreUpdateBackupDoesNotWriteArchives), TestConfigPaths.DefaultConfig);

        var update = await dinD.RunOnScratchAsync(scratch, nameof(UpdateWithSkipPreUpdateBackupDoesNotWriteArchives), "update-images",
            "--service", "msg-db", "--service", "scylla",
            "--skip-pre-update-backup");
        await Assert.That(update.ExitCode).IsEqualTo(0).Because($"update-images failed: {update.Stderr}");

        var pgAfter = await dinD.CountFilesAsync(scratch, "backups/postgres/*.dump");
        await Assert.That(pgAfter).IsEqualTo(0)
            .Because("--skip-pre-update-backup must suppress the backup step");

        var scyllaAfter = await dinD.CountFilesAsync(scratch, "backups/scylla/*.tar.gz");
        await Assert.That(scyllaAfter).IsEqualTo(0)
            .Because("--skip-pre-update-backup must suppress both components' archives");

        await Assert.That(update.Stdout + update.Stderr).Contains("skip-pre-update-backup")
            .Because("the phase must warn loudly when the escape hatch is used");
    }

    [Test]
    public async Task UpdateWithoutComposeFailsClearly()
    {
        // Running update-images against a bare scratch (no compose, no secrets, no
        // running stack) must fail with an actionable error rather than dying inside
        // docker compose. This is the "operator ran update-images before bootstrap"
        // misuse case — the phase runs several prerequisite checks and any of them
        // gives us the "guide to bootstrap" contract.
        var scratch = await dinD.CreateScratchAsync(nameof(UpdateWithoutComposeFailsClearly), TestConfigPaths.DefaultConfig);

        var result = await dinD.RunOnScratchAsync(scratch, nameof(UpdateWithoutComposeFailsClearly), "update-images");

        await Assert.That(result.ExitCode).IsNotEqualTo(0)
            .Because("update-images against a bare scratch must fail");

        var combined = result.Stdout + result.Stderr;
        await Assert.That(combined).Contains("bootstrap")
            .Because("error must guide the operator to run `bootstrap` first");
    }
}



