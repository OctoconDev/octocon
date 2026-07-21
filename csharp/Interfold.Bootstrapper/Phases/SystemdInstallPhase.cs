using System.Text;
using Interfold.Bootstrapper.Cli;
using Interfold.Bootstrapper.Configuration;
using Interfold.Bootstrapper.Util;

namespace Interfold.Bootstrapper.Phases;

/// <summary>
/// Installs (and optionally enables) the systemd units that own the boot-up
/// autostart, scheduled-backup, and image-update wiring:
/// <list type="bullet">
///   <item><c>interfold.service</c> — Type=oneshot RemainAfterExit=yes; brings the compose
///         stack up via <c>docker compose -f {outputDir}/docker-compose.yaml up -d</c>.</item>
///   <item><c>interfold-backup.service</c> — Type=oneshot; runs
///         <c>interfold-bootstrap backup</c>.</item>
///   <item><c>interfold-backup.timer</c> — fires the backup service on the schedule from
///         <see cref="BackupSection.Schedule"/>.</item>
///   <item><c>interfold-update.service</c> — Type=oneshot; runs
///         <c>interfold-bootstrap update-images</c>. Never scheduled directly —
///         invoked either manually or via the OnSuccess= chain from the backup unit.</item>
/// </list>
/// When <see cref="UpdateSection.Enabled"/> is true the phase additionally writes a drop-in
/// at <c>/etc/systemd/system/interfold-backup.service.d/50-chain-update.conf</c> with an
/// <c>OnSuccess=interfold-update.service</c> directive so a successful backup triggers an
/// update pass. That drop-in requires systemd >= 249 (Ubuntu 22.04+, Debian 12+, Fedora
/// 35+); the phase runs a preflight version check and refuses to install the chain on
/// older systemd with a clear error.
/// <para>
/// Templates ship as <c>systemd/*</c> manifest resources (see Interfold.Bootstrapper.csproj
/// — distinct from the <c>support/</c> prefix consumed by
/// <see cref="Util.EmbeddedSupportFiles"/>) so the unrendered template strings never land
/// on disk where an operator might enable them by mistake.
/// </para>
/// </summary>
internal static class SystemdInstallPhase
{
    private static readonly string Phase = BootstrapCommand.InstallService.ToPhaseLogName();

    /// <summary>Default systemd unit installation directory on every supported distro.</summary>
    private const string DefaultUnitDir = "/etc/systemd/system";

    /// <summary>Default basename for the bootstrapper binary inside the install dir.</summary>
    private const string DefaultBinaryName = "interfold-bootstrap";

    /// <summary>
    /// Minimum systemd major version required for the <c>OnSuccess=</c> drop-in that
    /// chains <c>interfold-update.service</c> onto <c>interfold-backup.service</c>.
    /// Older systemd (247 and below, e.g. Ubuntu 20.04) rejects the directive; the
    /// preflight in <see cref="RunAsync"/> refuses to install the drop-in on those
    /// hosts and instructs the operator to upgrade or invoke update-images manually.
    /// </summary>
    internal const int MinSystemdVersionForOnSuccess = 249;

    /// <summary>Names of the units this phase installs, in install order.</summary>
    internal static readonly string[] UnitNames =
    [
        SystemdUnitNames.Interfold,
        SystemdUnitNames.Backup,
        SystemdUnitNames.BackupTimer,
        SystemdUnitNames.Update,
    ];

    public static async Task<int> RunAsync(BootstrapOptions options, PhaseLogger logger, CancellationToken ct)
    {
        logger.PhaseStart(Phase);

        var config = await PhaseArtifactLoader
            .LoadRequiredConfigAsync(options, logger, Phase, "install-service", ct)
            .ConfigureAwait(false);
        var configPath = BootstrapArtifactPaths.ResolveConfigPath(options);

        var unitDir = options.SystemdUnitDir ?? DefaultUnitDir;
        var binaryPath = ResolveBinaryPath(options);
        var composeFile = ResolveComposeFile(options);

        var renderInput = new SystemdRenderInput(
            OutputDir: options.OutputDir,
            ComposeFile: composeFile,
            ConfigPath: Path.GetFullPath(configPath),
            BinaryPath: binaryPath,
            OnCalendar: config.Backup.Schedule);

        logger.Info($"    rendering units to {unitDir}");
        Directory.CreateDirectory(unitDir);

        foreach (var unitName in UnitNames)
        {
            var rendered = RenderUnit(unitName, renderInput);
            var destination = Path.Combine(unitDir, unitName);
            await File.WriteAllTextAsync(destination, rendered, ct).ConfigureAwait(false);
            logger.Info($"    wrote {destination}");
        }

        // Update chaining drop-in. We install this ONLY when the operator opted in via
        // config.update.enabled — otherwise the update service exists but is never
        // triggered by anything. The drop-in requires systemd >= 249 for OnSuccess=;
        // the preflight below refuses to install it on older systemd instead of
        // silently writing a directive systemd will ignore or error on at load time.
        if (config.Update.Enabled)
        {
            await EnsureSystemdSupportsOnSuccessAsync(logger, ct).ConfigureAwait(false);
            await WriteBackupOnSuccessDropInAsync(unitDir, logger, ct).ConfigureAwait(false);
        }
        else
        {
            // Idempotency: if a previous install-service call wrote the drop-in and the
            // operator has since flipped update.enabled=false, remove the stale file so
            // a subsequent scheduled backup no longer fires the update chain.
            RemoveBackupOnSuccessDropInIfPresent(unitDir, logger);
        }

        // systemd-analyze verify catches typos, missing tokens, and invalid directives
        // before we ever hand the unit to systemd. Skip silently when the binary isn't
        // available (tests on a docker-less Windows host) so the renderer path still has
        // coverage; production hosts always ship systemd-analyze with the systemd package.
        if (await ProcessRunner.ExistsOnPathAsync("systemd-analyze", ct).ConfigureAwait(false))
        {
            await VerifyAllUnitsAsync(unitDir, logger, ct).ConfigureAwait(false);
            await VerifyCalendarAsync(config.Backup.Schedule, logger, ct).ConfigureAwait(false);
        }
        else
        {
            logger.Warn("systemd-analyze not on PATH; skipping unit verification " +
                        "(install the systemd package on the target host).");
        }

        // The enable decision is layered: explicit --enable-* flags win, then fall back to
        // the matching config toggles. This means an operator who set
        // backup.enabled=true in interfold.bootstrap.json gets the timer enabled on a
        // plain `install-service` invocation, while a CI test passing --systemd-unit-dir
        // for verification only can omit the enable flags entirely.
        var enableAutostart = options.EnableAutostart || config.Backup.AutostartServer;
        var enableBackupTimer = options.EnableBackupTimer || config.Backup.Enabled;

        if (await ProcessRunner.ExistsOnPathAsync("systemctl", ct).ConfigureAwait(false)
            && options.SystemdUnitDir is null)
        {
            // Only daemon-reload and enable when writing to the real /etc/systemd/system/.
            // The test path (--systemd-unit-dir=<tmp>) skips this so it doesn't perturb the
            // host's systemd state.
            await SystemctlAsync(["daemon-reload"], logger, ct).ConfigureAwait(false);

            if (enableAutostart)
            {
                logger.Info("    enabling interfold.service");
                await SystemctlAsync(["enable", "--now", SystemdUnitNames.Interfold], logger, ct).ConfigureAwait(false);
            }
            if (enableBackupTimer)
            {
                logger.Info("    enabling interfold-backup.timer");
                await SystemctlAsync(["enable", "--now", SystemdUnitNames.BackupTimer], logger, ct).ConfigureAwait(false);
            }
        }
        else if (options.SystemdUnitDir is not null)
        {
            logger.Info("    --systemd-unit-dir set; skipping daemon-reload/enable (test mode)");
        }
        else
        {
            logger.Warn("systemctl not on PATH; units written but not enabled. " +
                        "Run `systemctl daemon-reload` then `systemctl enable --now <unit>` manually.");
        }

        logger.PhaseDone(Phase);
        return 0;
    }

    /// <summary>
    /// Token-substitution input bundle. Plain record so the unit-test project can drive
    /// <see cref="RenderUnit"/> against bespoke inputs without staging a real
    /// <see cref="BootstrapOptions"/>.
    /// </summary>
    internal sealed record SystemdRenderInput(
        string OutputDir,
        string ComposeFile,
        string ConfigPath,
        string BinaryPath,
        string OnCalendar);

    /// <summary>
    /// Reads the embedded template for <paramref name="unitName"/>, replaces every
    /// <c>{{TOKEN}}</c> with the matching field from <paramref name="input"/>, and returns
    /// the rendered text. Internal so the renderer can be unit-tested in isolation from
    /// the install path (which writes to disk + shells out to systemctl).
    /// </summary>
    internal static string RenderUnit(string unitName, SystemdRenderInput input)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(unitName);
        ArgumentNullException.ThrowIfNull(input);

        var resourceName = $"systemd/{unitName}";
        var asm = typeof(SystemdInstallPhase).Assembly;
        using var stream = asm.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded systemd template '{resourceName}' missing. " +
                "Check Interfold.Bootstrapper.csproj's <EmbeddedResource> entries.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var template = reader.ReadToEnd();

        var sb = new StringBuilder(template);
        // Order matters: the renderer must accept every token the bundled templates
        // reference. If a future template adds a new token without a matching replacement
        // here, the rendered file will still contain a literal "{{NEW_TOKEN}}" and
        // systemd-analyze will catch it — but better to fail fast with a clear C#
        // exception. SanityCheck below enforces that contract.
        sb.Replace("{{OUTPUT_DIR}}", input.OutputDir);
        sb.Replace("{{COMPOSE_FILE}}", input.ComposeFile);
        sb.Replace("{{CONFIG_PATH}}", input.ConfigPath);
        sb.Replace("{{BINARY_PATH}}", input.BinaryPath);
        sb.Replace("{{ON_CALENDAR}}", input.OnCalendar);
        var rendered = sb.ToString();

        if (rendered.Contains("{{", StringComparison.Ordinal))
        {
            // Strip the noise around the residual token for a tighter error message.
            var openIdx = rendered.IndexOf("{{", StringComparison.Ordinal);
            var closeIdx = rendered.IndexOf("}}", openIdx, StringComparison.Ordinal);
            var snippet = closeIdx > openIdx
                ? rendered.Substring(openIdx, closeIdx - openIdx + 2)
                : rendered[openIdx..Math.Min(rendered.Length, openIdx + 40)];
            throw new InvalidOperationException(
                $"systemd template '{unitName}' contains an unsubstituted token '{snippet}'. " +
                "Add the matching replacement to SystemdInstallPhase.RenderUnit.");
        }

        return rendered;
    }

    private static string ResolveBinaryPath(BootstrapOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.BinaryPathOverride))
        {
            return Path.GetFullPath(options.BinaryPathOverride);
        }
        // The published binary is `interfold-bootstrap` (AssemblyName in the csproj). When
        // the bootstrapper is invoked via `dotnet ./interfold-bootstrap.dll` we still want
        // to point the systemd unit at the .NET-loaded entry-point binary alongside that
        // dll; AppContext.BaseDirectory + "interfold-bootstrap" is the canonical install
        // layout the README documents.
        return Path.Combine(AppContext.BaseDirectory, DefaultBinaryName);
    }

    private static string ResolveComposeFile(BootstrapOptions options)
        => BootstrapArtifactPaths.ResolveComposeFileOrConventional(options.OutputDir);

    private static async Task VerifyAllUnitsAsync(string unitDir, PhaseLogger logger, CancellationToken ct)
    {
        foreach (var unitName in UnitNames)
        {
            var unitPath = Path.Combine(unitDir, unitName);
            var verify = await ProcessRunner.RunAsync(
                "systemd-analyze", ["verify", unitPath], ct: ct).ConfigureAwait(false);
            if (verify.ExitCode != 0)
            {
                logger.PhaseFail(Phase, PhaseFailureReasons.SystemdAnalyzeVerify);
                throw new InvalidOperationException(
                    $"systemd-analyze verify failed for {unitPath} (exit {verify.ExitCode}):\n" +
                    $"{verify.StdErr.Trim()}\n{verify.StdOut.Trim()}");
            }
            logger.Info($"    verified {unitName}");
        }
    }

    private static async Task VerifyCalendarAsync(string schedule, PhaseLogger logger, CancellationToken ct)
    {
        var verify = await ProcessRunner.RunAsync(
            "systemd-analyze", ["calendar", schedule], ct: ct).ConfigureAwait(false);
        if (verify.ExitCode != 0)
        {
            logger.PhaseFail(Phase, PhaseFailureReasons.InvalidCalendar);
            throw new InvalidOperationException(
                $"systemd-analyze calendar '{schedule}' rejected the schedule (exit {verify.ExitCode}):\n" +
                $"{verify.StdErr.Trim()}\n{verify.StdOut.Trim()}");
        }
        logger.Info($"    calendar '{schedule}' parses OK");
    }

    private static async Task SystemctlAsync(IReadOnlyList<string> args, PhaseLogger logger, CancellationToken ct)
    {
        var run = await ProcessRunner.RunAsync("systemctl", args, ct: ct).ConfigureAwait(false);
        if (run.ExitCode != 0)
        {
            logger.PhaseFail(Phase, PhaseFailureReasons.Systemctl);
            throw new InvalidOperationException(
                $"systemctl {string.Join(' ', args)} exited {run.ExitCode}: {run.StdErr.Trim()}");
        }
        if (!string.IsNullOrWhiteSpace(run.StdOut)) logger.Info(run.StdOut.Trim());
    }

    /// <summary>
    /// On-disk name of the systemd drop-in that adds <c>OnSuccess=interfold-update.service</c>
    /// to <c>interfold-backup.service</c>. The <c>50-</c> prefix follows the systemd
    /// convention for operator-installed drop-ins so a hand-written override at
    /// e.g. <c>90-local.conf</c> still wins.
    /// </summary>
    internal const string BackupOnSuccessDropInDir = "interfold-backup.service.d";

    /// <summary>File basename for the <see cref="BackupOnSuccessDropInDir"/> drop-in.</summary>
    internal const string BackupOnSuccessDropInFile = "50-chain-update.conf";

    /// <summary>
    /// Writes the <c>OnSuccess=interfold-update.service</c> drop-in that chains the update
    /// service onto a successful backup run. Template rendered verbatim (no
    /// <c>{{TOKEN}}</c> substitutions needed). The drop-in directory is created if missing;
    /// re-runs safely overwrite the file.
    /// </summary>
    internal static async Task WriteBackupOnSuccessDropInAsync(
        string unitDir, PhaseLogger logger, CancellationToken ct)
    {
        var dropInDir = Path.Combine(unitDir, BackupOnSuccessDropInDir);
        Directory.CreateDirectory(dropInDir);
        var content = ReadEmbeddedTemplate("interfold-backup-onsuccess.conf");
        var destination = Path.Combine(dropInDir, BackupOnSuccessDropInFile);
        await File.WriteAllTextAsync(destination, content, ct).ConfigureAwait(false);
        logger.Info($"    wrote {destination} (chains interfold-update.service after successful backup)");
    }

    /// <summary>
    /// Removes any previously-installed drop-in from a prior <c>update.enabled=true</c>
    /// run. Called on <c>update.enabled=false</c> installs so an operator that opts out
    /// after opting in doesn't get surprised by lingering chain behaviour. No-op when
    /// the file (or directory) isn't present.
    /// </summary>
    internal static void RemoveBackupOnSuccessDropInIfPresent(string unitDir, PhaseLogger logger)
    {
        var destination = Path.Combine(unitDir, BackupOnSuccessDropInDir, BackupOnSuccessDropInFile);
        if (!File.Exists(destination)) return;
        try
        {
            File.Delete(destination);
            logger.Info($"    removed stale {destination} (config.update.enabled=false)");
        }
        catch (Exception ex)
        {
            logger.Warn($"failed to delete stale drop-in {destination}: {ex.Message}");
        }
    }

    private static string ReadEmbeddedTemplate(string basename)
    {
        var resourceName = $"systemd/{basename}";
        var asm = typeof(SystemdInstallPhase).Assembly;
        using var stream = asm.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded template '{resourceName}' missing. " +
                "Check Interfold.Bootstrapper.csproj's <EmbeddedResource> entries.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Fails the phase if the running systemd is too old to accept the
    /// <c>OnSuccess=</c> directive. Systemd introduced OnSuccess= / OnFailure=Job=
    /// in v249 (Ubuntu 22.04 ships 249, Debian 12 ships 252, Fedora 35+ ships 249+).
    /// Skipped silently when <c>systemctl</c> isn't on PATH — the test path routes
    /// through <c>--systemd-unit-dir</c> against a temp dir where a missing
    /// <c>systemctl</c> is normal.
    /// </summary>
    internal static async Task EnsureSystemdSupportsOnSuccessAsync(PhaseLogger logger, CancellationToken ct)
    {
        if (!await ProcessRunner.ExistsOnPathAsync("systemctl", ct).ConfigureAwait(false))
        {
            logger.Warn("systemctl not on PATH; skipping systemd version preflight " +
                        "for OnSuccess= chaining. If the target systemd is older than " +
                        $"{MinSystemdVersionForOnSuccess}, the drop-in will fail at daemon-reload.");
            return;
        }

        var run = await ProcessRunner.RunAsync("systemctl", ["--version"], ct: ct).ConfigureAwait(false);
        if (run.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"systemctl --version exited {run.ExitCode} while checking OnSuccess= support: {run.StdErr.Trim()}");
        }

        var version = ParseSystemdMajorVersion(run.StdOut);
        if (version is null)
        {
            logger.Warn(
                "could not parse systemd version from `systemctl --version` output; " +
                $"proceeding with OnSuccess= drop-in install anyway. Requires systemd >= {MinSystemdVersionForOnSuccess}.");
            return;
        }
        if (version.Value < MinSystemdVersionForOnSuccess)
        {
            throw new InvalidOperationException(
                $"config.update.enabled=true requires systemd >= {MinSystemdVersionForOnSuccess} " +
                $"(the target host is running systemd {version.Value}). " +
                "Upgrade to Ubuntu 22.04+, Debian 12+, or Fedora 35+, or leave config.update.enabled=false " +
                "and invoke `update-images` manually.");
        }
        logger.Info($"    systemd {version.Value} supports OnSuccess= chaining");
    }

    /// <summary>
    /// Extracts the major systemd version integer from <c>systemctl --version</c>
    /// output. The first line is canonically <c>systemd &lt;N&gt; (&lt;codename&gt;)</c>
    /// (e.g. <c>systemd 249 (249.11-0ubuntu3.16)</c>); returns null if the line
    /// doesn't parse, so callers can gracefully degrade rather than hard-fail on an
    /// unfamiliar systemd fork. Internal for unit-test coverage.
    /// </summary>
    internal static int? ParseSystemdMajorVersion(string versionOutput)
    {
        if (string.IsNullOrWhiteSpace(versionOutput)) return null;
        var firstLine = versionOutput.Split('\n', 2)[0].Trim();
        // Expected: "systemd 249 (249.11-0ubuntu3.16)" — split on whitespace, take token 1.
        var parts = firstLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return null;
        if (!parts[0].Equals("systemd", StringComparison.Ordinal)) return null;
        // Some distros embed the version as "249.11" in field 1 — strip past the first dot.
        var token = parts[1];
        var dot = token.IndexOf('.');
        if (dot > 0) token = token[..dot];
        return int.TryParse(token, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
    }

}
