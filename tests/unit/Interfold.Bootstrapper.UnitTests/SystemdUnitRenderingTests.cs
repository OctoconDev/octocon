using Interfold.Bootstrapper.Cli;
using Interfold.Bootstrapper.Phases;

namespace Interfold.Bootstrapper.UnitTests;

/// <summary>
/// Asserts the token-substitution semantics of <see cref="SystemdInstallPhase.RenderUnit"/>.
/// We never shell out to systemd-analyze here — that's the integration test's job. These
/// just pin the rendering contract so a future template tweak doesn't silently drop a
/// substitution.
/// </summary>
public sealed class SystemdUnitRenderingTests
{
    /// <summary>Default render input used by every test below; tests override one field at a time.</summary>
    private static SystemdInstallPhase.SystemdRenderInput MakeInput() => new(
        OutputDir: "/srv/interfold/deploy",
        ComposeFile: "/srv/interfold/deploy/docker-compose.yaml",
        ConfigPath: "/srv/interfold/deploy/interfold.bootstrap.json",
        BinaryPath: "/opt/interfold/interfold-bootstrap",
        OnCalendar: "daily");

    [Test]
    public async Task InterfoldServiceContainsExpectedExecStart()
    {
        var rendered = SystemdInstallPhase.RenderUnit("interfold.service", MakeInput());

        // The boot-time autostart is deliberately a direct `docker compose up -d` rather
        // than `interfold-bootstrap up` — the bootstrapper's up path waits up to 5 minutes
        // on /health/ready, which would block the boot critical path.
        await Assert.That(rendered).Contains("ExecStart=/usr/bin/docker compose -f /srv/interfold/deploy/docker-compose.yaml up -d");
        await Assert.That(rendered).Contains("ExecStop=/usr/bin/docker compose -f /srv/interfold/deploy/docker-compose.yaml down");
        await Assert.That(rendered).Contains("WorkingDirectory=/srv/interfold/deploy");
        await Assert.That(rendered).Contains("Type=oneshot");
        await Assert.That(rendered).Contains("RemainAfterExit=yes");
        await Assert.That(rendered).Contains("After=docker.service network-online.target");
        await Assert.That(rendered).Contains("Requires=docker.service");
        await Assert.That(rendered).Contains("WantedBy=multi-user.target");
    }

    [Test]
    public async Task InterfoldBackupServiceContainsExpectedExecStart()
    {
        var rendered = SystemdInstallPhase.RenderUnit("interfold-backup.service", MakeInput());

        // The backup service runs the bootstrapper's `backup` subcommand and points at the
        // operator's resolved config + outputDir; the timer fires it on the schedule
        // operators configure via OnCalendar=.
        await Assert.That(rendered).Contains(
            "ExecStart=/opt/interfold/interfold-bootstrap backup " +
            "--config /srv/interfold/deploy/interfold.bootstrap.json " +
            "--output-dir /srv/interfold/deploy " +
            "--component all");
        await Assert.That(rendered).Contains("Type=oneshot");
        // Backup runs after the server is up — operator-visible documentation says the
        // dependency is "soft", so Wants= (not Requires=) is correct.
        await Assert.That(rendered).Contains("Wants=interfold.service");
        await Assert.That(rendered).Contains("After=interfold.service");
    }

    [Test]
    public async Task InterfoldBackupTimerEmbedsOnCalendar()
    {
        var input = MakeInput() with { OnCalendar = "Mon..Fri 03:30" };
        var rendered = SystemdInstallPhase.RenderUnit("interfold-backup.timer", input);

        await Assert.That(rendered).Contains("OnCalendar=Mon..Fri 03:30");
        await Assert.That(rendered).Contains("Persistent=true");
        await Assert.That(rendered).Contains("Unit=interfold-backup.service");
        await Assert.That(rendered).Contains("WantedBy=timers.target");
    }

    [Test]
    public async Task NoUnsubstitutedTokensRemainInAnyUnit()
    {
        // The rendered unit text must never contain `{{` (the template token sentinel).
        // RenderUnit itself throws when it spots a residual token, so this also serves as
        // a smoke test that the renderer's own validation isn't tripping on legitimate text.
        var input = MakeInput();
        foreach (var unitName in SystemdInstallPhase.UnitNames)
        {
            var rendered = SystemdInstallPhase.RenderUnit(unitName, input);
            await Assert.That(rendered).DoesNotContain("{{");
            await Assert.That(rendered).DoesNotContain("}}");
        }
    }

    [Test]
    public async Task RenderUnitThrowsForUnknownUnitName()
    {
        // Defensive: a typo in SystemdInstallPhase.UnitNames would otherwise produce a
        // confusing null-stream error from GetManifestResourceStream. The explicit
        // exception message names the bad template so the maintainer sees the typo.
        var ex = Assert.Throws<InvalidOperationException>(
            () => SystemdInstallPhase.RenderUnit("does-not-exist.service", MakeInput()));
        await Assert.That(ex.Message).Contains("does-not-exist.service");
    }

    [Test]
    public async Task RenderUnitPreservesScheduleSpecialCharacters()
    {
        // systemd accepts comma-separated lists, colons, asterisks, slashes, and ranges in
        // OnCalendar expressions. The renderer must not mangle any of them — the
        // BackupSchedulePattern regex in ConfigPhase.Validate permits them all, so the
        // rendered text must too.
        var input = MakeInput() with { OnCalendar = "*-*-* 02,14:00:00" };
        var rendered = SystemdInstallPhase.RenderUnit("interfold-backup.timer", input);

        await Assert.That(rendered).Contains("OnCalendar=*-*-* 02,14:00:00");
    }

    [Test]
    public async Task RenderUnitWorksForAllRegisteredUnitNames()
    {
        // Smoke: every name listed in UnitNames must have a matching embedded resource so
        // the install loop doesn't crash partway through. Catches a future "added a unit
        // to UnitNames but forgot to add the template" mistake.
        var input = MakeInput();
        foreach (var unitName in SystemdInstallPhase.UnitNames)
        {
            var rendered = SystemdInstallPhase.RenderUnit(unitName, input);
            await Assert.That(rendered.Length).IsGreaterThan(0)
                .Because($"template '{unitName}' must render to a non-empty unit body");
        }
    }

    [Test]
    public async Task InterfoldUpdateServiceContainsExpectedExecStart()
    {
        // The update service delegates to `interfold-bootstrap update-images` and points
        // at the operator's resolved config + outputDir. Chained to backup via the
        // OnSuccess= drop-in when config.update.enabled=true; also invocable directly.
        var rendered = SystemdInstallPhase.RenderUnit("interfold-update.service", MakeInput());

        await Assert.That(rendered).Contains(
            "ExecStart=/opt/interfold/interfold-bootstrap update-images " +
            "--config /srv/interfold/deploy/interfold.bootstrap.json " +
            "--output-dir /srv/interfold/deploy");
        await Assert.That(rendered).Contains("Type=oneshot");
        await Assert.That(rendered).Contains("WorkingDirectory=/srv/interfold/deploy");
        // Same "soft" dependency shape as the backup service — the update is a runtime
        // operation, not part of the boot critical path.
        await Assert.That(rendered).Contains("Wants=interfold.service");
    }

    [Test]
    public async Task UpdateServiceIsInUnitNames()
    {
        // Pin that interfold-update.service is part of the canonical install set so
        // SystemdInstallPhase writes it on every install-service invocation. Actually
        // enabling / chaining it is a separate decision (config.update.enabled).
        await Assert.That(SystemdInstallPhase.UnitNames).Contains("interfold-update.service");
    }

    [Test]
    public async Task WhenUpdateEnabledDropInIsWritten()
    {
        // The drop-in template is copied verbatim (no token substitution), so this test
        // exercises the write path directly against a temp unit dir. Confirms the file
        // lands at the systemd-conventional path and contains the OnSuccess= directive.
        using var scratch = TestSupport.NewScratchDir("interfold-dropin");
        var tmp = scratch.Path;
        var logger = new PhaseLogger(TestSupport.MakeOptions(
            command: BootstrapCommand.InstallService,
            outputDir: tmp,
            nonInteractive: true));

        await SystemdInstallPhase.WriteBackupOnSuccessDropInAsync(tmp, logger, CancellationToken.None);

        var dropInPath = Path.Combine(tmp,
            SystemdInstallPhase.BackupOnSuccessDropInDir,
            SystemdInstallPhase.BackupOnSuccessDropInFile);
        await Assert.That(File.Exists(dropInPath)).IsTrue()
            .Because($"drop-in must be written to {dropInPath}");
        var content = await File.ReadAllTextAsync(dropInPath);
        await Assert.That(content).Contains("[Unit]");
        await Assert.That(content).Contains("OnSuccess=interfold-update.service");
    }

    [Test]
    public async Task RemoveDropInIsNoOpWhenAbsent()
    {
        // Idempotency: an install-service with update.enabled=false against a host that
        // never had the drop-in must not throw. RemoveIfPresent silently no-ops when
        // the file (or the directory) is missing.
        using var scratch = TestSupport.NewScratchDir("interfold-dropin-abs");
        var tmp = scratch.Path;
        var logger = new PhaseLogger(TestSupport.MakeOptions(
            command: BootstrapCommand.InstallService,
            outputDir: tmp,
            nonInteractive: true));

        SystemdInstallPhase.RemoveBackupOnSuccessDropInIfPresent(tmp, logger);

        var dropInPath = Path.Combine(tmp,
            SystemdInstallPhase.BackupOnSuccessDropInDir,
            SystemdInstallPhase.BackupOnSuccessDropInFile);
        await Assert.That(File.Exists(dropInPath)).IsFalse();
    }

    [Test]
    public async Task RemoveDropInDeletesExistingFile()
    {
        // The other half of the idempotency contract: a previous install wrote the
        // drop-in and the operator has since flipped update.enabled=false. On the
        // next install-service run we must delete the stale file so a scheduled
        // backup no longer fires the update chain.
        using var scratch = TestSupport.NewScratchDir("interfold-dropin-del");
        var tmp = scratch.Path;
        var dropInDir = Path.Combine(tmp, SystemdInstallPhase.BackupOnSuccessDropInDir);
        Directory.CreateDirectory(dropInDir);
        var dropInPath = Path.Combine(dropInDir, SystemdInstallPhase.BackupOnSuccessDropInFile);
        await File.WriteAllTextAsync(dropInPath, "[Unit]\nOnSuccess=interfold-update.service\n");

        var logger = new PhaseLogger(TestSupport.MakeOptions(
            command: BootstrapCommand.InstallService,
            outputDir: tmp,
            nonInteractive: true));

        SystemdInstallPhase.RemoveBackupOnSuccessDropInIfPresent(tmp, logger);

        await Assert.That(File.Exists(dropInPath)).IsFalse();
    }

    [Test]
    public async Task ParseSystemdMajorVersionAcceptsUbuntuFormat()
    {
        // Ubuntu 22.04 output: "systemd 249 (249.11-0ubuntu3.16)".
        await Assert.That(SystemdInstallPhase.ParseSystemdMajorVersion("systemd 249 (249.11-0ubuntu3.16)\n+PAM +AUDIT ..."))
            .IsEqualTo(249);
    }

    [Test]
    public async Task ParseSystemdMajorVersionAcceptsFedoraFormat()
    {
        // Fedora 40 output: "systemd 255 (255.10-1.fc40)".
        await Assert.That(SystemdInstallPhase.ParseSystemdMajorVersion("systemd 255 (255.10-1.fc40)"))
            .IsEqualTo(255);
    }

    [Test]
    public async Task ParseSystemdMajorVersionAcceptsDottedToken()
    {
        // Some builds emit "systemd 253.1"; strip past the first dot.
        await Assert.That(SystemdInstallPhase.ParseSystemdMajorVersion("systemd 253.1 (253.1-1)"))
            .IsEqualTo(253);
    }

    [Test]
    public async Task ParseSystemdMajorVersionRejectsMalformed()
    {
        await Assert.That(SystemdInstallPhase.ParseSystemdMajorVersion(""))
            .IsNull();
        await Assert.That(SystemdInstallPhase.ParseSystemdMajorVersion("something completely unrelated"))
            .IsNull();
        await Assert.That(SystemdInstallPhase.ParseSystemdMajorVersion("systemd"))
            .IsNull();
    }
}
