using Interfold.Bootstrapper.IntegrationTests.Attributes;
using Interfold.Bootstrapper.IntegrationTests.Fixtures;

namespace Interfold.Bootstrapper.IntegrationTests;

/// <summary>
/// Integration tests for the <c>install-service</c> subcommand. The shared Ubuntu DinD
/// fixture deliberately keeps PID 1 as <c>dockerd-entrypoint.sh</c> (not <c>systemd</c>),
/// so we exercise the unit-file rendering + on-disk write path against an operator-supplied
/// unit dir (<c>--systemd-unit-dir</c>) and skip the daemon-reload / enable steps. The
/// <c>systemd</c> package IS pre-installed in <c>Dockerfile.ubuntu-dind</c> — the binary
/// (specifically <c>systemd-analyze</c>) works fine outside of a systemd-managed PID 1
/// and is what <see cref="SystemdAnalyzeAcceptsRenderedUnits"/> and the bootstrapper's
/// install-service phase both rely on. <c>jq</c> is also baked into the image so
/// <see cref="OverlayUpdateConfigAsync"/> can rewrite the config JSON without any test
/// having to shell out to <c>apt-get install</c> at runtime.
/// </summary>
[RequiresDocker]
[ClassDataSource<UbuntuDinDFixture>(Shared = SharedType.PerTestSession)]
public class SystemdInstallTests(UbuntuDinDFixture dinD)
{

    [After(Test)]
    public Task DumpOnFailure(TestContext ctx) => DinDHookHelpers.DumpOnFailureAsync(dinD, ctx);

    [Test]
    public async Task WritesAllThreeUnitFilesToTargetDir()
    {
        // We don't need a live compose stack for install-service — it only reads the bootstrap
        // config + writes unit files. Skip the full bootstrap to keep the test fast.
        var scratch = await dinD.CreateScratchAsync(nameof(WritesAllThreeUnitFilesToTargetDir), TestConfigPaths.DefaultConfig);
        var unitDir = $"{scratch.Root}/systemd-units";
        await dinD.ExecAsync(["mkdir", "-p", unitDir]);

        var result = await dinD.RunOnScratchAsync(scratch, nameof(WritesAllThreeUnitFilesToTargetDir), "install-service",
            "--systemd-unit-dir", unitDir);
        await Assert.That(result.ExitCode).IsEqualTo(0).Because($"install-service failed: {result.Stderr}");

        // Each of the four units must be present on disk. interfold-update.service is
        // always rendered — the OnSuccess drop-in that enables the chain is what depends
        // on update.enabled, but the target unit exists regardless so manual invocations
        // (`update-images`) always have a service to point at.
        foreach (var name in new[] { "interfold.service", "interfold-backup.service", "interfold-backup.timer", "interfold-update.service" })
        {
            var probe = await dinD.ExecAsync(["test", "-f", $"{unitDir}/{name}"]);
            await Assert.That(probe.ExitCode).IsEqualTo(0L)
                .Because($"expected {unitDir}/{name} to exist after install-service");
        }
    }

    [Test]
    public async Task UnitFilesContainExpectedTokens()
    {
        // Pin the rendered contents: the boot service must use docker compose up -d (not
        // `interfold-bootstrap up`), the backup service must point at the resolved config +
        // outputDir, and the timer must embed the OnCalendar string from the config.
        var scratch = await dinD.CreateScratchAsync(nameof(UnitFilesContainExpectedTokens), TestConfigPaths.DefaultConfig);
        var unitDir = $"{scratch.Root}/systemd-units";
        await dinD.ExecAsync(["mkdir", "-p", unitDir]);

        var result = await dinD.RunOnScratchAsync(scratch, nameof(UnitFilesContainExpectedTokens), "install-service",
            "--systemd-unit-dir", unitDir,
            "--binary-path", "/opt/bootstrapper/interfold-bootstrap");
        await Assert.That(result.ExitCode).IsEqualTo(0).Because($"install-service failed: {result.Stderr}");

        var bootService = await dinD.CopyOutAsTextAsync($"{unitDir}/interfold.service");
        await Assert.That(bootService).Contains($"ExecStart=/usr/bin/docker compose -f {scratch.OutputDir}/docker-compose.yaml up -d");
        await Assert.That(bootService).Contains("WantedBy=multi-user.target");

        var backupService = await dinD.CopyOutAsTextAsync($"{unitDir}/interfold-backup.service");
        await Assert.That(backupService).Contains("ExecStart=/opt/bootstrapper/interfold-bootstrap backup");
        await Assert.That(backupService).Contains($"--config {scratch.ConfigPath}");
        await Assert.That(backupService).Contains($"--output-dir {scratch.OutputDir}");

        var timer = await dinD.CopyOutAsTextAsync($"{unitDir}/interfold-backup.timer");
        // The test fixture's config doesn't override the schedule, so it lands at the default
        // "daily" from BackupSection.Schedule.
        await Assert.That(timer).Contains("OnCalendar=daily");
        await Assert.That(timer).Contains("Persistent=true");
    }

    [Test]
    public async Task WritesUpdateServiceWhenPresentInUnitNames()
    {
        // interfold-update.service is always rendered alongside the three baseline units.
        // Even when config.update.enabled=false the .service file exists (manual
        // `update-images` invocations use it); only the OnSuccess= drop-in is conditional.
        var scratch = await dinD.CreateScratchAsync(nameof(WritesUpdateServiceWhenPresentInUnitNames), TestConfigPaths.DefaultConfig);
        var unitDir = $"{scratch.Root}/systemd-units";
        await dinD.ExecAsync(["mkdir", "-p", unitDir]);

        var result = await dinD.RunOnScratchAsync(scratch, nameof(WritesUpdateServiceWhenPresentInUnitNames), "install-service",
            "--systemd-unit-dir", unitDir,
            "--binary-path", "/opt/bootstrapper/interfold-bootstrap");
        await Assert.That(result.ExitCode).IsEqualTo(0).Because($"install-service failed: {result.Stderr}");

        var probe = await dinD.ExecAsync(["test", "-f", $"{unitDir}/interfold-update.service"]);
        await Assert.That(probe.ExitCode).IsEqualTo(0L)
            .Because("interfold-update.service should always be rendered even when update.enabled=false");

        var updateService = await dinD.CopyOutAsTextAsync($"{unitDir}/interfold-update.service");
        await Assert.That(updateService).Contains("ExecStart=/opt/bootstrapper/interfold-bootstrap update-images");
        await Assert.That(updateService).Contains($"--config {scratch.ConfigPath}");
        await Assert.That(updateService).Contains($"--output-dir {scratch.OutputDir}");
        await Assert.That(updateService).Contains("Type=oneshot");
    }

    [Test]
    public async Task WhenUpdateEnabledDropInIsWritten()
    {
        // Flip update.enabled=true in a copy of the test config, run install-service, and
        // assert the OnSuccess drop-in appears at the exact path SystemdInstallPhase
        // documents (interfold-backup.service.d/50-chain-update.conf) with the correct
        // OnSuccess= directive.
        var scratch = await dinD.CreateScratchAsync(nameof(WhenUpdateEnabledDropInIsWritten), TestConfigPaths.DefaultConfig);
        var unitDir = $"{scratch.Root}/systemd-units";
        await dinD.ExecAsync(["mkdir", "-p", unitDir]);

        await OverlayUpdateConfigAsync(scratch.ConfigPath, updateEnabled: true);

        var result = await dinD.RunOnScratchAsync(scratch, nameof(WhenUpdateEnabledDropInIsWritten), "install-service",
            "--systemd-unit-dir", unitDir,
            "--binary-path", "/opt/bootstrapper/interfold-bootstrap");
        await Assert.That(result.ExitCode).IsEqualTo(0).Because($"install-service failed: {result.Stderr}");

        var dropInPath = $"{unitDir}/interfold-backup.service.d/50-chain-update.conf";
        var probe = await dinD.ExecAsync(["test", "-f", dropInPath]);
        await Assert.That(probe.ExitCode).IsEqualTo(0L)
            .Because($"expected drop-in {dropInPath} when update.enabled=true");

        var content = await dinD.CopyOutAsTextAsync(dropInPath);
        await Assert.That(content).Contains("OnSuccess=interfold-update.service")
            .Because("drop-in exists to chain the update service after a successful backup");
        await Assert.That(content).Contains("[Unit]")
            .Because("drop-in must be a valid systemd unit fragment");
    }

    [Test]
    public async Task WhenUpdateDisabledDropInIsAbsent()
    {
        // Baseline config leaves update.enabled=false — the drop-in must NOT be written,
        // even though the .service template it references was rendered. Guards the
        // "manual updates only" default stance so operators who never touch the update
        // section don't wake up to auto-chained updates.
        var scratch = await dinD.CreateScratchAsync(nameof(WhenUpdateDisabledDropInIsAbsent), TestConfigPaths.DefaultConfig);
        var unitDir = $"{scratch.Root}/systemd-units";
        await dinD.ExecAsync(["mkdir", "-p", unitDir]);

        var result = await dinD.RunOnScratchAsync(scratch, nameof(WhenUpdateDisabledDropInIsAbsent), "install-service",
            "--systemd-unit-dir", unitDir,
            "--binary-path", "/opt/bootstrapper/interfold-bootstrap");
        await Assert.That(result.ExitCode).IsEqualTo(0).Because($"install-service failed: {result.Stderr}");

        var probe = await dinD.ExecAsync(["test", "-e", $"{unitDir}/interfold-backup.service.d/50-chain-update.conf"]);
        await Assert.That(probe.ExitCode).IsNotEqualTo(0L)
            .Because("drop-in must be absent when update.enabled=false (default stance)");
    }

    [Test]
    public async Task DropInIsRemovedWhenUpdateFlippedOff()
    {
        // Two-phase run: first install with update.enabled=true (writes the drop-in),
        // then re-install with update.enabled=false and assert the drop-in was removed.
        // Pins the idempotency contract — flipping the config off must clean up stale
        // drop-ins so a subsequent backup no longer fires the update chain.
        var scratch = await dinD.CreateScratchAsync(nameof(DropInIsRemovedWhenUpdateFlippedOff), TestConfigPaths.DefaultConfig);
        var unitDir = $"{scratch.Root}/systemd-units";
        await dinD.ExecAsync(["mkdir", "-p", unitDir]);

        // Phase 1: install with update.enabled=true.
        await OverlayUpdateConfigAsync(scratch.ConfigPath, updateEnabled: true);
        var install1 = await dinD.RunOnScratchAsync(scratch, $"{nameof(DropInIsRemovedWhenUpdateFlippedOff)}-1", "install-service",
            "--systemd-unit-dir", unitDir,
            "--binary-path", "/opt/bootstrapper/interfold-bootstrap");
        await Assert.That(install1.ExitCode).IsEqualTo(0).Because(install1.Stderr);

        var probeExists = await dinD.ExecAsync(["test", "-f", $"{unitDir}/interfold-backup.service.d/50-chain-update.conf"]);
        await Assert.That(probeExists.ExitCode).IsEqualTo(0L)
            .Because("phase 1 should have written the drop-in");

        // Phase 2: flip off and re-install.
        await OverlayUpdateConfigAsync(scratch.ConfigPath, updateEnabled: false);
        var install2 = await dinD.RunOnScratchAsync(scratch, $"{nameof(DropInIsRemovedWhenUpdateFlippedOff)}-2", "install-service",
            "--systemd-unit-dir", unitDir,
            "--binary-path", "/opt/bootstrapper/interfold-bootstrap");
        await Assert.That(install2.ExitCode).IsEqualTo(0).Because(install2.Stderr);

        var probeGone = await dinD.ExecAsync(["test", "-e", $"{unitDir}/interfold-backup.service.d/50-chain-update.conf"]);
        await Assert.That(probeGone.ExitCode).IsNotEqualTo(0L)
            .Because("phase 2 must have removed the stale drop-in when update.enabled flipped to false");
    }

    /// <summary>
    /// Rewrites the in-DinD config file to set (or unset) <c>update.enabled</c>. Reads the
    /// existing config, merges an <c>update</c> block via <c>jq</c> (baked into
    /// <c>Dockerfile.ubuntu-dind</c> alongside <c>systemd</c>), and writes it back atomically
    /// so a follow-up install-service run sees the flipped flag.
    /// </summary>
    /// <remarks>
    /// <c>jq</c> lives in the image on purpose — before that, this helper installed it
    /// on-demand under <c>&gt;/dev/null 2&gt;&amp;1</c>, which raced with
    /// <c>SystemdAnalyzeAcceptsRenderedUnits</c>' equivalent <c>apt-get install systemd</c>
    /// on <c>/var/lib/dpkg/lock-frontend</c> and produced silent <c>exit 100</c> flakes in CI
    /// whenever TUnit interleaved the two tests. Moving both installs into the image kills
    /// the race and makes the helper synchronous / assertion-free.
    /// </remarks>
    private async Task OverlayUpdateConfigAsync(string configPath, bool updateEnabled)
    {
        var enabledLiteral = updateEnabled ? "true" : "false";
        // Merge (+ operator) so any pre-existing update block wins on collision except for
        // enabled, which the outer setter forces. Writes to a sibling tempfile then moves
        // into place so partial writes don't leave the config half-parsed.
        var script =
            $"jq '. + {{ update: ((.update // {{}}) + {{ enabled: {enabledLiteral} }}) }}' {configPath} " +
            $"> {configPath}.tmp && mv {configPath}.tmp {configPath}";
        var overlay = await dinD.ExecAsync(["sh", "-c", script]);
        await Assert.That(overlay.ExitCode).IsEqualTo(0L)
            .Because($"overlaying update.enabled={updateEnabled} onto {configPath} failed: {overlay.Stderr}");
    }

    [Test]
    public async Task SystemdAnalyzeAcceptsRenderedUnits()
    {
        // Runs `systemd-analyze verify` against every rendered unit via the bootstrapper's
        // install-service phase. The DinD's PID 1 is dockerd-entrypoint, not systemd — that's
        // fine, systemd-analyze is a static parser that doesn't need a running systemd to run.
        // The `systemd` apt package (which ships systemd-analyze) is baked into
        // Dockerfile.ubuntu-dind alongside `jq` — a previous incarnation of this test installed
        // it on-demand under `>/dev/null 2>&1` and raced with OverlayUpdateConfigAsync's jq
        // install on `/var/lib/dpkg/lock-frontend`, producing silent exit-100 flakes in CI.
        var scratch = await dinD.CreateScratchAsync(nameof(SystemdAnalyzeAcceptsRenderedUnits), TestConfigPaths.DefaultConfig);
        var unitDir = $"{scratch.Root}/systemd-units";
        await dinD.ExecAsync(["mkdir", "-p", unitDir]);

        var result = await dinD.RunOnScratchAsync(scratch, nameof(SystemdAnalyzeAcceptsRenderedUnits), "install-service",
            "--systemd-unit-dir", unitDir,
            "--binary-path", "/opt/bootstrapper/interfold-bootstrap");
        await Assert.That(result.ExitCode).IsEqualTo(0).Because($"install-service failed: {result.Stderr}");

        // The phase itself runs systemd-analyze verify when the binary is on PATH; confirm via
        // the captured logs that it did so and accepted every unit.
        await Assert.That(result.Stdout + result.Stderr).Contains("verified interfold.service")
            .Because("install-service should report a successful verification line for interfold.service");
        await Assert.That(result.Stdout + result.Stderr).Contains("verified interfold-backup.service");
        await Assert.That(result.Stdout + result.Stderr).Contains("verified interfold-backup.timer");
        await Assert.That(result.Stdout + result.Stderr).Contains("verified interfold-update.service")
            .Because("install-service should verify the new update service alongside the baseline three");
        await Assert.That(result.Stdout + result.Stderr).Contains("calendar 'daily' parses OK")
            .Because("install-service should report systemd-analyze acceptance for the schedule");
    }
}



