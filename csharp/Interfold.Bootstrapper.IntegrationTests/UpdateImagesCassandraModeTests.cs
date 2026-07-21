using Interfold.Bootstrapper.IntegrationTests.Attributes;
using Interfold.Bootstrapper.IntegrationTests.Fixtures;
using TUnit.Core;

namespace Interfold.Bootstrapper.IntegrationTests;

/// <summary>
/// Cassandra-mode integration tests for the <c>update-images</c> subcommand. Sibling of
/// <see cref="UpdateImagesPhaseTests"/> (which exercises the scylla-mode compose stack).
/// These tests pin the two behaviours unique to <c>databaseMode=cassandra</c>:
/// <list type="bullet">
///   <item>
///     <c>bootstrap publish</c> stamps <c>pull_policy: never</c> onto the cassandra
///     service in the emitted <c>docker-compose.yaml</c>, so <c>docker compose pull</c>
///     skips the local-only <c>interfold-cassandra:local</c> tag instead of failing.
///   </item>
///   <item>
///     <c>update-images</c> rebuilds <c>interfold-cassandra:local</c> before the pull
///     (so Dockerfile changes take effect via update-images alone), scoped to whether
///     the effective service whitelist includes <c>cassandra</c>.
///   </item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// Uses the dedicated <see cref="UbuntuCassandraDinDFixture"/> so the extra
/// <c>cassandra:5</c> base image is pre-pulled once per session — the DinD is otherwise
/// network-isolated (the API image is loaded from a bind-mounted tar), so a first-run
/// pull of <c>cassandra:5</c> inside the test would exceed the fixture's warm-up budget.
/// </para>
/// <para>
/// The health-check side of <c>update-images</c> (post-pull compose up + nodetool status
/// probe) is deliberately not asserted here — cassandra:5 takes ~90s to reach UN in a
/// DinD environment, which would dominate the CI runtime. The scylla-mode sibling suite
/// covers the health-check code path; these tests scope to the cassandra-specific
/// build + pull-skip contract by driving `update-images` with the whitelist narrowed
/// to non-cassandra services (so no cassandra recreate happens) OR by asserting on the
/// pull-phase output before recreate begins.
/// </para>
/// </remarks>
[RequiresDocker]
[ClassDataSource<UbuntuCassandraDinDFixture>(Shared = SharedType.PerTestSession)]
public class UpdateImagesCassandraModeTests(UbuntuCassandraDinDFixture dinD)
{

    [After(Test)]
    public Task DumpOnFailure(TestContext ctx) => DinDHookHelpers.DumpOnFailureAsync(dinD, ctx);

    [Test]
    public async Task PublishInCassandraModeStampsPullPolicyOnComposeYaml()
    {
        // The narrowest test in the file: run just the `publish` subcommand (no compose up),
        // then read the emitted docker-compose.yaml and assert the cassandra service block
        // carries `pull_policy: never`. Fast — no health check, no image pull, no compose up.
        // Locks down PublishPhase.StampCassandraPullPolicyNever's happy-path contract in a
        // real end-to-end publish, not just the unit test's synthetic YAML.
        var (scratch, composeFile) = await dinD.PublishAsync(nameof(PublishInCassandraModeStampsPullPolicyOnComposeYaml), TestConfigPaths.CassandraConfig, "--skip-prereqs");

        // grep -A 1 pulls the line after each `${CASSANDRA_IMAGE}` match; we assert the
        // policy shows up in that immediate neighbourhood. Portable across compose-yaml
        // layout tweaks (indent, blank lines, extra volume keys under the service) as long
        // as pull_policy: never lands next to the image key.
        var grep = await dinD.ExecAsync(["sh", "-c",
            $"grep -A 1 -F '${{CASSANDRA_IMAGE}}' {scratch.OutputDir}/docker-compose.yaml"]);
        await Assert.That(grep.ExitCode).IsEqualTo(0L)
            .Because($"the cassandra service must contain the ${{CASSANDRA_IMAGE}} anchor: {grep.Stderr}");
        await Assert.That(grep.Stdout).Contains("pull_policy: never")
            .Because("PublishPhase.StampCassandraPullPolicyNever must land pull_policy: never right after the image key");
    }

    [Test]
    public async Task UpdateInCassandraModeSkipsCassandraOnPullAndRebuildsLocalImage()
    {
        // End-to-end pull-side contract: after a full bootstrap in cassandra mode, an
        // update-images run must (1) rebuild interfold-cassandra:local before pulling
        // (log line "cassandra mode: rebuilding") and (2) let `docker compose pull`
        // skip the cassandra service natively via pull_policy: never rather than
        // failing on the non-registry-backed tag.
        //
        // We scope the update to msg-db (a registry-backed TimescaleDB image the DinD
        // has cached) + cassandra via --service. The msg-db entry gives compose pull
        // something registry-backed to work against so it produces observable output;
        // the cassandra entry drives ShouldRebuildCassandra's "cassandra in scope"
        // branch. interfold-api is deliberately excluded — interfold-api:test is a
        // locally-loaded tag with no registry manifest, so including it would cause
        // `docker compose pull` to exit non-zero with "pull access denied" before the
        // cassandra pull-skip evidence could land. See the class remarks on
        // UpdateImagesPhaseTests for the general "locally-built API image" caveat.
        var (scratch, _) = await dinD.BootstrapAsync(nameof(UpdateInCassandraModeSkipsCassandraOnPullAndRebuildsLocalImage), TestConfigPaths.CassandraConfig);

        // Sanity: the local image was built by bootstrap. `docker images -q` returns
        // the image ID (non-empty) if the tag exists locally.
        var imgBefore = await dinD.ExecAsync(["sh", "-c", "docker images -q interfold-cassandra:local"]);
        await Assert.That(imgBefore.Stdout.Trim().Length).IsGreaterThan(0)
            .Because("bootstrap should have built interfold-cassandra:local via CassandraImagePhase.EnsureBuiltAsync");

        var update = await dinD.RunOnScratchAsync(scratch, nameof(UpdateInCassandraModeSkipsCassandraOnPullAndRebuildsLocalImage), "update-images",
            "--service", "msg-db", "--service", "cassandra",
            "--skip-pre-update-backup");
        await Assert.That(update.ExitCode).IsEqualTo(0).Because($"update-images failed: {update.Stderr}");

        var combined = update.Stdout + update.Stderr;

        // Rebuild log line: proves ShouldRebuildCassandra + EnsureBuiltAsync fired.
        await Assert.That(combined).Contains("cassandra mode: rebuilding interfold-cassandra:local")
            .Because("update-images in cassandra mode with cassandra in scope must rebuild the local image");

        // Pull-skip evidence: docker compose reports the cassandra service as "Skipped"
        // (compose behaviour when pull_policy: never — see pkg/compose/pull.go on
        // docker/compose main). Absent this, the pull would fail with a manifest-not-found
        // error on interfold-cassandra:local, so seeing exit 0 above is already necessary
        // proof; the string match here pins the branch as well.
        await Assert.That(combined).Contains("Skipped")
            .Because("`docker compose pull` should skip the cassandra service due to pull_policy: never");

        // Image is still present after the update (rebuild is idempotent at the Docker
        // layer-cache level, so the ID may or may not have changed depending on cache
        // hits; either way the tag must resolve).
        var imgAfter = await dinD.ExecAsync(["sh", "-c", "docker images -q interfold-cassandra:local"]);
        await Assert.That(imgAfter.Stdout.Trim().Length).IsGreaterThan(0)
            .Because("interfold-cassandra:local must still exist after the update-images rebuild");
    }

    [Test]
    public async Task UpdateInCassandraModeWithMsgDbWhitelistSkipsRebuild()
    {
        // The scoping guard: narrowing an update with `--service msg-db` in cassandra mode
        // must NOT trigger an unrelated Cassandra rebuild. Locks down ShouldRebuildCassandra's
        // "whitelist excludes cassandra" branch in a real invocation.
        var (scratch, _) = await dinD.BootstrapAsync(nameof(UpdateInCassandraModeWithMsgDbWhitelistSkipsRebuild), TestConfigPaths.CassandraConfig);

        var update = await dinD.RunOnScratchAsync(scratch, nameof(UpdateInCassandraModeWithMsgDbWhitelistSkipsRebuild), "update-images",
            "--service", "msg-db",
            "--skip-pre-update-backup");
        await Assert.That(update.ExitCode).IsEqualTo(0).Because($"update-images failed: {update.Stderr}");

        var combined = update.Stdout + update.Stderr;
        await Assert.That(combined).DoesNotContain("cassandra mode: rebuilding")
            .Because("--service msg-db must not trigger a cassandra rebuild");
    }
}




