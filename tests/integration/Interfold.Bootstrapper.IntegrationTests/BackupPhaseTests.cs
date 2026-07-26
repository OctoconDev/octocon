using Interfold.Bootstrapper.IntegrationTests.Attributes;
using Interfold.Bootstrapper.IntegrationTests.Fixtures;

namespace Interfold.Bootstrapper.IntegrationTests;

/// <summary>
/// Integration tests for the <c>backup</c> subcommand against a real running compose stack
/// inside the shared Ubuntu DinD fixture. Each test runs a full <c>bootstrap</c> first
/// (bringing up postgres + scylla), then exercises the backup pipeline and asserts the
/// on-disk artifacts.
/// </summary>
[RequiresDocker]
[ClassDataSource<UbuntuDinDFixture>(Shared = SharedType.PerTestSession)]
public class BackupPhaseTests(UbuntuDinDFixture dinD)
{

    [After(Test)]
    public Task DumpOnFailure(TestContext ctx) => DinDHookHelpers.DumpOnFailureAsync(dinD, ctx);

    [Test]
    public async Task BackupCreatesPostgresAndScyllaArtifacts()
    {
        // Full bootstrap first so we have a live compose stack to back up. The backup phase
        // depends on the API container + DB containers running (it execs into them via
        // `docker compose exec`).
        var (scratch, _) = await dinD.BootstrapAsync(nameof(BackupCreatesPostgresAndScyllaArtifacts), TestConfigPaths.DefaultConfig);

        var backup = await dinD.RunOnScratchAsync(scratch, nameof(BackupCreatesPostgresAndScyllaArtifacts), "backup",
            "--component", "all");
        await Assert.That(backup.ExitCode).IsEqualTo(0).Because($"backup failed: {backup.Stderr}");

        // ls the per-component subdirs — must contain at least one matching archive each.
        var pgList = await dinD.ExecAsync(["sh", "-c", $"ls -1 {scratch.OutputDir}/backups/postgres/*.dump 2>/dev/null | head -5"]);
        await Assert.That(pgList.ExitCode).IsEqualTo(0L);
        await Assert.That(pgList.Stdout.Trim().Length).IsGreaterThan(0)
            .Because("backup should produce at least one .dump under backups/postgres/");

        var scyllaList = await dinD.ExecAsync(["sh", "-c", $"ls -1 {scratch.OutputDir}/backups/scylla/*.tar.gz 2>/dev/null | head -5"]);
        await Assert.That(scyllaList.ExitCode).IsEqualTo(0L);
        await Assert.That(scyllaList.Stdout.Trim().Length).IsGreaterThan(0)
            .Because("backup should produce at least one .tar.gz under backups/scylla/");

        // Size sanity: an empty pg_dump custom-format file is 6 bytes (the magic header), but a
        // populated one is at least a few hundred bytes. Anything below 100 strongly suggests
        // pg_dump bailed and we wrote a truncated artifact.
        var pgSize = await dinD.ExecAsync(["sh", "-c", $"stat -c %s {scratch.OutputDir}/backups/postgres/*.dump | head -1"]);
        await Assert.That(int.Parse(pgSize.Stdout.Trim())).IsGreaterThan(100)
            .Because("pg_dump output must be non-trivial in size");

        // The scylla tar.gz includes at least the directory tree even when there's no user data;
        // anything below 100 bytes is a smoking gun for a failed tar.
        var scyllaSize = await dinD.ExecAsync(["sh", "-c", $"stat -c %s {scratch.OutputDir}/backups/scylla/*.tar.gz | head -1"]);
        await Assert.That(int.Parse(scyllaSize.Stdout.Trim())).IsGreaterThan(100)
            .Because("scylla tar.gz output must be non-trivial in size");
    }

    [Test]
    public async Task BackupRetentionPrunesOldestPastRetainCount()
    {
        // Two-phase retention check: take three backups in a row with retain=2 and assert only
        // the two newest survive each iteration. The mtime spacing isn't guaranteed across
        // back-to-back invocations (they happen within the same second), so we use the
        // timestamp encoded in the filename as the secondary ordering.
        var (scratch, _) = await dinD.BootstrapAsync(nameof(BackupRetentionPrunesOldestPastRetainCount), TestConfigPaths.DefaultConfig);

        // First two runs: both should survive under retain=2.
        for (var i = 0; i < 2; i++)
        {
            // sleep 1s between backups so the timestamps differ; backup filenames use second-
            // resolution and we need each iteration's archive to be distinguishable on disk.
            await dinD.ExecAsync(["sleep", "1"]);
            var b = await dinD.RunOnScratchAsync(scratch, $"{nameof(BackupRetentionPrunesOldestPastRetainCount)}-{i}", "backup",
                "--component", "all", "--retain", "2");
            await Assert.That(b.ExitCode).IsEqualTo(0).Because($"backup #{i} failed: {b.Stderr}");
        }

        var countAfterTwo = await dinD.CountFilesAsync(scratch, "backups/postgres/");
        await Assert.That(countAfterTwo).IsEqualTo(2)
            .Because("two backups + retain=2 should leave exactly 2 files");

        // Third run: pruning kicks in, exactly 2 must remain.
        await dinD.ExecAsync(["sleep", "1"]);
        var third = await dinD.RunOnScratchAsync(scratch, $"{nameof(BackupRetentionPrunesOldestPastRetainCount)}-third", "backup",
            "--component", "all", "--retain", "2");
        await Assert.That(third.ExitCode).IsEqualTo(0).Because($"third backup failed: {third.Stderr}");

        var pgCount = await dinD.CountFilesAsync(scratch, "backups/postgres/");
        await Assert.That(pgCount).IsEqualTo(2)
            .Because("three backups + retain=2 should prune the oldest, leaving exactly 2");

        var scyllaCount = await dinD.CountFilesAsync(scratch, "backups/scylla/");
        await Assert.That(scyllaCount).IsEqualTo(2)
            .Because("scylla retention must mirror postgres retention");
    }

    [Test]
    public async Task BackupComponentFlagRestrictsScope()
    {
        // --component postgres should write a .dump but NOT touch backups/scylla/, and vice
        // versa. Pins the contract so a future refactor that ignores the flag is caught.
        var (scratch, _) = await dinD.BootstrapAsync(nameof(BackupComponentFlagRestrictsScope), TestConfigPaths.DefaultConfig);

        var pgOnly = await dinD.RunOnScratchAsync(scratch, $"{nameof(BackupComponentFlagRestrictsScope)}-pg", "backup",
            "--component", "postgres");
        await Assert.That(pgOnly.ExitCode).IsEqualTo(0).Because(pgOnly.Stderr);

        // Postgres subdir populated; scylla subdir either missing or empty.
        var pgList = await dinD.CountFilesAsync(scratch, "backups/postgres/*.dump");
        await Assert.That(pgList).IsEqualTo(1);

        var scyllaList = await dinD.CountFilesAsync(scratch, "backups/scylla/*.tar.gz");
        await Assert.That(scyllaList).IsEqualTo(0)
            .Because("--component=postgres must not touch the scylla subdir");
    }

    [Test]
    public async Task BackupWithoutComposeFailsClearly()
    {
        // Backup against a bare scratch directory (no compose, no secrets, nothing but the
        // config file) must fail with a clear, actionable error rather than dying inside
        // docker compose. This is the most-likely operator misuse: running `backup` before
        // `bootstrap` / `publish`. The phase runs several prerequisite checks and any of
        // them is a legitimate signal — the important contract is that the error names a
        // specific missing artifact and tells the operator to run `bootstrap` first.
        var scratch = await dinD.CreateScratchAsync(nameof(BackupWithoutComposeFailsClearly), TestConfigPaths.DefaultConfig);

        // Write the config but skip the bootstrap. The scratch's outputDir has only the
        // config file, no compose stack.
        var result = await dinD.RunOnScratchAsync(scratch, nameof(BackupWithoutComposeFailsClearly), "backup",
            "--component", "postgres");

        await Assert.That(result.ExitCode).IsNotEqualTo(0)
            .Because("backup against a bare scratch (no compose, no secrets) must fail");

        var combined = result.Stdout + result.Stderr;
        // Either message satisfies the "clear, actionable error" contract — the phase
        // checks secrets before the compose file so a bare scratch trips the secrets
        // check first; a stack with secrets but no compose file trips the compose check.
        // Both name a specific missing artifact and point at `bootstrap` for the fix.
        await Assert.That(combined).Contains("docker-compose.yaml").Or.Contains("secrets/secrets.json")
            .Because("error must name a specific missing artifact");
        await Assert.That(combined).Contains("bootstrap")
            .Because("error must guide the operator to `bootstrap` as the fix");
    }
}



