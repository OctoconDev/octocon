using System.Text;
using Interfold.Bootstrapper.Cli;
using Interfold.Bootstrapper.Configuration;
using Interfold.Bootstrapper.Util;

namespace Interfold.Bootstrapper.Phases;

/// <summary>Installs (and optionally enables) the systemd units for autostart, scheduled
/// backup, and image update: <c>interfold.service</c> (compose <c>up -d</c>),
/// <c>interfold-backup.service</c>/.timer, and <c>interfold-update.service</c>. When
/// <see cref="UpdateSection.Enabled"/> is set, also writes an
/// <c>OnSuccess=interfold-update.service</c> drop-in that chains update onto backup —
/// requires systemd >= 249 (preflight refuses older hosts). Templates ship as
/// <c>systemd/*</c> manifest resources so unrendered strings never land on disk.</summary>
internal static class SystemdInstallPhase
{
    private static readonly string Phase = BootstrapCommand.InstallService.ToPhaseLogName();

    private const string DefaultUnitDir = "/etc/systemd/system";
    private const string DefaultBinaryName = "interfold-bootstrap";

    /// <summary>Minimum systemd for <c>OnSuccess=</c> (Ubuntu 22.04+, Debian 12+, Fedora 35+).</summary>
    internal const int MinSystemdVersionForOnSuccess = 249;

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

        if (config.Update.Enabled)
        {
            await EnsureSystemdSupportsOnSuccessAsync(logger, ct).ConfigureAwait(false);
            await WriteBackupOnSuccessDropInAsync(unitDir, logger, ct).ConfigureAwait(false);
        }
        else
        {
            // Idempotency for operators toggling update.enabled back off.
            RemoveBackupOnSuccessDropInIfPresent(unitDir, logger);
        }

        // Skip silently on hosts without systemd-analyze (Windows CI) to keep the
        // renderer covered; production hosts always ship it with systemd.
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

        // --enable-* CLI flags beat the matching config toggles.
        var enableAutostart = options.EnableAutostart || config.Backup.AutostartServer;
        var enableBackupTimer = options.EnableBackupTimer || config.Backup.Enabled;

        if (await ProcessRunner.ExistsOnPathAsync("systemctl", ct).ConfigureAwait(false)
            && options.SystemdUnitDir is null)
        {
            // Only touch the real /etc/systemd/system; test path (--systemd-unit-dir) stays inert.
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

    /// <summary>Token-substitution input for <see cref="RenderUnit"/>, driven directly by
    /// unit tests without a live <see cref="BootstrapOptions"/>.</summary>
    internal sealed record SystemdRenderInput(
        string OutputDir,
        string ComposeFile,
        string ConfigPath,
        string BinaryPath,
        string OnCalendar);

    /// <summary>Reads the embedded template, substitutes every <c>{{TOKEN}}</c>, and returns
    /// the rendered text. Internal so tests can drive the renderer without touching disk.</summary>
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
        // Every token in the shipped templates must have a matching replacement here; the
        // "{{" residue check below fails fast on a new template token without a mapping.
        sb.Replace("{{OUTPUT_DIR}}", input.OutputDir);
        sb.Replace("{{COMPOSE_FILE}}", input.ComposeFile);
        sb.Replace("{{CONFIG_PATH}}", input.ConfigPath);
        sb.Replace("{{BINARY_PATH}}", input.BinaryPath);
        sb.Replace("{{ON_CALENDAR}}", input.OnCalendar);
        var rendered = sb.ToString();

        if (rendered.Contains("{{", StringComparison.Ordinal))
        {
            // Trim the residual token for a tighter error message.
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
        // Canonical install layout the README documents.
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

    /// <summary><c>50-</c> prefix follows systemd convention so operator overrides
    /// (e.g. <c>90-local.conf</c>) still win.</summary>
    internal const string BackupOnSuccessDropInDir = "interfold-backup.service.d";

    internal const string BackupOnSuccessDropInFile = "50-chain-update.conf";

    /// <summary>Writes the <c>OnSuccess=interfold-update.service</c> chain drop-in verbatim.
    /// Idempotent: re-runs overwrite the file.</summary>
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

    /// <summary>Removes a previously-installed chain drop-in when the operator opts out.
    /// No-op when the file (or directory) is absent.</summary>
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

    /// <summary>Fails if the running systemd is older than
    /// <see cref="MinSystemdVersionForOnSuccess"/>. Skipped silently when systemctl is
    /// missing (test path against a tmp unit dir).</summary>
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

    /// <summary>Parses <c>systemd &lt;N&gt;</c> from the first line of
    /// <c>systemctl --version</c>; null on unrecognised output so callers degrade
    /// gracefully on forks.</summary>
    internal static int? ParseSystemdMajorVersion(string versionOutput)
    {
        if (string.IsNullOrWhiteSpace(versionOutput)) return null;
        var firstLine = versionOutput.Split('\n', 2)[0].Trim();
        var parts = firstLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return null;
        if (!parts[0].Equals("systemd", StringComparison.Ordinal)) return null;
        // Some distros embed a "249.11" style; strip past the first dot.
        var token = parts[1];
        var dot = token.IndexOf('.');
        if (dot > 0) token = token[..dot];
        return int.TryParse(token, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
    }

}
