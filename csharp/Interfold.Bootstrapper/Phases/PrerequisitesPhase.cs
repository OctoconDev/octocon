using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using Interfold.Bootstrapper.Cli;
using Interfold.Bootstrapper.Util;
using Interfold.Contracts.Enums;

namespace Interfold.Bootstrapper.Phases;

/// <summary>
/// Phase 1 — verifies platform support and installs host-side prerequisites:
/// Docker Engine + Compose plugin, openssl, and a persistent <c>fs.aio-max-nr</c> setting
/// (required by Scylla / Seastar startup).
/// </summary>
internal static partial class PrerequisitesPhase
{
    // Per-Scylla-node Seastar AIO budget. Source of truth: Seastar's own startup error
    // ("Set /proc/sys/fs/aio-max-nr to at least 66563 (minimum) or 116562 (recommended for
    // networking performance)"). Keep these in sync with scripts/docker/ensure-host-aio.sh
    // and csharp/Interfold.IntegrationTests/TestServices/HostAioPrerequisite.cs.
    private const int AioPerNodeMin = 66_563;
    private const int AioPerNodeRecommended = 116_562;
    private const int AioHeadroom = 50_000;
    private const string AioSysctlPath = "/proc/sys/fs/aio-max-nr";
    private const string SysctlDropIn = "/etc/sysctl.d/99-interfold.conf";

    /// <summary>
    /// Maps an operator's <see cref="Configuration.BootstrapConfig.DatabaseMode"/> value to the
    /// number of Scylla nodes the deployment will run on the host, for AIO sizing. Cassandra
    /// uses its own (non-Seastar) IO path so it doesn't count against the Seastar AIO budget.
    /// Kept as a raw-string overload so <see cref="PeekScyllaNodeCountAsync"/> can size AIO from
    /// the on-disk JSON before <c>ConfigPhase</c> has validated it.
    /// </summary>
    internal static int ResolveScyllaNodeCount(string? databaseMode)
        // TryParseWire returns false for null/empty/whitespace/unknown; falling back to
        // Single preserves the historical "anything else sizes for one node" behaviour.
        => ResolveScyllaNodeCount(
            databaseMode.TryParseWire<DatabaseMode>(out var mode) ? mode : DatabaseMode.Single);

    /// <summary>
    /// Typed overload used once <see cref="Configuration.BootstrapConfig.DatabaseMode"/> has been
    /// validated. Same table as the raw-string version — kept separate so callers with a bound
    /// enum don't have to round-trip through <c>ToWireValue()</c>.
    /// </summary>
    internal static int ResolveScyllaNodeCount(DatabaseMode databaseMode) => databaseMode switch
    {
        DatabaseMode.Multi => 7,
        DatabaseMode.Cassandra => 0,
        _ => 1,
    };

    public static async Task RunAsync(BootstrapOptions options, PhaseLogger logger, CancellationToken ct)
    {
        string Phase = BootstrapPhase.Prereqs.ToWireName();
        logger.PhaseStart(Phase);

        EnsureLinux(logger);
        EnsureRoot(logger);

        var distro = DistroInfo.Read();
        logger.Info($"    detected distro: {distro.PrettyName ?? distro.Id} (family={distro.Family})");

        if (distro.Family == DistroFamily.Unknown)
        {
            logger.PhaseFail(Phase, PhaseFailureReasons.UnsupportedDistro);
            throw new InvalidOperationException(
                $"Unsupported Linux distribution '{distro.Id}'. Supported families: Debian/Ubuntu, RHEL/Fedora. " +
                "See docs/SELF_HOSTING.md for tested distros.");
        }

        await EnsureDockerAsync(distro, logger, ct).ConfigureAwait(false);
        await EnsureOpenSslAsync(distro, logger, ct).ConfigureAwait(false);

        // Peek at the operator's databaseMode to size AIO precisely for the topology that
        // ConfigPhase will validate next. ConfigPhase still owns full schema validation; this
        // is a deliberately tolerant read that defaults to a single Scylla node if anything
        // about the file is unexpected (missing, malformed, unrecognised mode). That matches
        // the existing behaviour where PrereqsPhase has historically used a 1-node-equivalent
        // baseline.
        var scyllaNodes = await PeekScyllaNodeCountAsync(options, logger, ct).ConfigureAwait(false);
        await EnsureAioLimitAsync(scyllaNodes, logger, ct).ConfigureAwait(false);

        logger.PhaseDone(Phase);
    }

    private static async Task<int> PeekScyllaNodeCountAsync(BootstrapOptions options, PhaseLogger logger, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(options.ConfigPath) || !File.Exists(options.ConfigPath))
        {
            return ResolveScyllaNodeCount(null);
        }

        try
        {
            await using var stream = File.OpenRead(options.ConfigPath);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            if (doc.RootElement.TryGetProperty("databaseMode", out var modeElement) &&
                modeElement.ValueKind == JsonValueKind.String)
            {
                return ResolveScyllaNodeCount(modeElement.GetString());
            }
        }
        catch (Exception ex)
        {
            // ConfigPhase will surface a useful error against the same file shortly; we just
            // fall back to a safe default for the AIO calculation.
            logger.Warn($"could not pre-read databaseMode from {options.ConfigPath} for AIO sizing ({ex.GetType().Name}); defaulting to single-node baseline.");
        }

        return ResolveScyllaNodeCount(null);
    }

    private static void EnsureLinux(PhaseLogger logger)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return;
        }
        logger.PhaseFail(BootstrapPhase.Prereqs.ToWireName(), PhaseFailureReasons.NonLinuxHost);
        throw new InvalidOperationException(
            "The bootstrapper is Linux-only. For local development use `aspire run` from " +
            "csharp/Interfold.AppHost instead.");
    }

    private static void EnsureRoot(PhaseLogger logger)
    {
        // geteuid()==0 means we're root (or operating with the necessary capabilities).
        // Single-line libc P/Invoke avoids a third-party Unix binding for one check.
        if (NativeMethods.geteuid() == 0) return;

        logger.PhaseFail(BootstrapPhase.Prereqs.ToWireName(), PhaseFailureReasons.NonRoot);
        throw new InvalidOperationException(
            "Run the bootstrapper with sudo. Installing Docker, writing to /etc/sysctl.d, " +
            "and editing the system trust store all require root.");
    }

    private static partial class NativeMethods
    {
        [LibraryImport("libc")]
        internal static partial uint geteuid();
    }

    private static async Task EnsureDockerAsync(DistroInfo distro, PhaseLogger logger, CancellationToken ct)
    {
        if (await ProcessRunner.ExistsOnPathAsync("docker", ct).ConfigureAwait(false))
        {
            // Already there - verify the compose plugin too.
            var compose = await ProcessRunner.RunAsync("docker", ["compose", "version"], ct: ct).ConfigureAwait(false);
            if (compose.ExitCode == 0)
            {
                logger.Info("    docker + compose plugin already present");
                return;
            }
            logger.Warn("docker found but `docker compose` is missing; installing compose plugin.");
        }

        logger.Info("    installing docker engine + compose plugin from docker.com...");
        // `docker-compose-plugin` is NOT in the stock Ubuntu / Debian / Fedora repos - it ships
        // exclusively from Docker's official package repository. Likewise the Ubuntu `docker.io`
        // package is older than what most operators need and doesn't include the v2 compose plugin
        // at all. So in both families we add Docker's official repo first, then install
        // `docker-ce` + the buildx/compose plugins from there. This mirrors the upstream
        // instructions at https://docs.docker.com/engine/install/ for each distro.
        switch (distro.Family)
        {
            case DistroFamily.Debian:
                await ConfigureDockerAptRepoAsync(distro, logger, ct).ConfigureAwait(false);
                await RunAptInstallAsync(
                    ["docker-ce", "docker-ce-cli", "containerd.io", "docker-buildx-plugin",
                     "docker-compose-plugin", "ca-certificates"],
                    logger, ct).ConfigureAwait(false);
                break;
            case DistroFamily.RedHat:
                await ConfigureDockerDnfRepoAsync(distro, logger, ct).ConfigureAwait(false);
                await RunDnfInstallAsync(
                    ["docker-ce", "docker-ce-cli", "containerd.io", "docker-buildx-plugin",
                     "docker-compose-plugin", "ca-certificates"],
                    logger, ct).ConfigureAwait(false);
                break;
            default:
                throw new InvalidOperationException($"Cannot install Docker on distro family {distro.Family}.");
        }

        // dockerd needs to be running for subsequent phases (`docker compose up`). On hosts that
        // ship without systemd (minimal containers, Alpine derivatives) we tolerate a missing
        // systemctl binary - the operator is responsible for starting dockerd manually in that
        // case. Wrapping the call in try/catch is necessary because Process.Start throws a
        // Win32Exception when the binary isn't on PATH, rather than returning a non-zero exit.
        try
        {
            await ProcessRunner.RunAsync("systemctl", ["enable", "--now", "docker"], ct: ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.Warn($"could not run `systemctl enable --now docker` ({ex.GetType().Name}: {ex.Message}); " +
                        "start dockerd manually if it isn't already running.");
        }
    }

    private static async Task ConfigureDockerAptRepoAsync(DistroInfo distro, PhaseLogger logger, CancellationToken ct)
    {
        // Docker only ships official apt repos for ubuntu and debian. Downstream debian-likes
        // (Mint, Pop!_OS, etc.) tend to track an ubuntu base, so we fall back to that when the
        // ID itself isn't directly supported.
        var dockerDistro = ResolveDebianFamilyDockerDistro(distro);
        var codename = distro.VersionCodename ?? throw new InvalidOperationException(
            $"Could not determine VERSION_CODENAME for {distro.PrettyName ?? distro.Id}. " +
            "The bootstrapper needs this to choose the correct Docker apt suite.");

        var arch = await ResolveDpkgArchAsync(ct).ConfigureAwait(false);

        logger.Info($"    configuring apt repo: download.docker.com/linux/{dockerDistro} suite={codename} arch={arch}");

        // /etc/apt/keyrings is the standard location for third-party keyrings on Debian 12+ /
        // Ubuntu 22.04+. `install -d` creates it (and any missing ancestors) with the right perms.
        var mkKeyring = await ProcessRunner.RunAsync(
            "install", ["-m", "0755", "-d", "/etc/apt/keyrings"], ct: ct).ConfigureAwait(false);
        if (mkKeyring.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Could not prepare /etc/apt/keyrings (exit {mkKeyring.ExitCode}): {mkKeyring.StdErr.Trim()}");
        }

        // Download Docker's release-signing key. We hold the file in memory rather than shelling
        // out to curl so the prereqs phase doesn't depend on curl being present (it usually is,
        // but on minimal hosts we still want a clean error path).
        const string KeyringPath = "/etc/apt/keyrings/docker.asc";
        var keyUrl = $"https://download.docker.com/linux/{dockerDistro}/gpg";
        await DownloadFileAsync(keyUrl, KeyringPath, ct).ConfigureAwait(false);
        var chmod = await ProcessRunner.RunAsync("chmod", ["a+r", KeyringPath], ct: ct).ConfigureAwait(false);
        if (chmod.ExitCode != 0)
        {
            // World-readable is what `apt-get update` needs; warn but keep going - apt itself will
            // surface a clearer error if the file actually isn't readable.
            logger.Warn($"chmod a+r {KeyringPath} exited {chmod.ExitCode}: {chmod.StdErr.Trim()}");
        }

        var sourcesLine =
            $"deb [arch={arch} signed-by={KeyringPath}] https://download.docker.com/linux/{dockerDistro} {codename} stable\n";
        await File.WriteAllTextAsync("/etc/apt/sources.list.d/docker.list", sourcesLine, ct).ConfigureAwait(false);
    }

    private static async Task ConfigureDockerDnfRepoAsync(DistroInfo distro, PhaseLogger logger, CancellationToken ct)
    {
        // Docker publishes rhel, fedora, and centos yum/dnf repos. Downstream RHEL clones (Rocky,
        // AlmaLinux) are binary-compatible with rhel, so we route them there. The .repo file is
        // self-contained: it includes the GPG key URL and signature settings, so we don't need a
        // separate keyring step like we do on debian.
        var dockerDistro = ResolveRedHatFamilyDockerDistro(distro);
        var repoUrl = $"https://download.docker.com/linux/{dockerDistro}/docker-ce.repo";
        const string RepoPath = "/etc/yum.repos.d/docker-ce.repo";

        logger.Info($"    configuring dnf repo: {repoUrl}");
        await DownloadFileAsync(repoUrl, RepoPath, ct).ConfigureAwait(false);
    }

    private static string ResolveDebianFamilyDockerDistro(DistroInfo distro)
    {
        var id = distro.Id.ToLowerInvariant();
        if (id is "ubuntu" or "debian") return id;
        var likes = (distro.IdLike ?? string.Empty).ToLowerInvariant();
        if (likes.Contains("ubuntu")) return "ubuntu";
        if (likes.Contains("debian")) return "debian";
        // Best-effort fallback: most modern debian-likes are ubuntu-based.
        return "ubuntu";
    }

    private static string ResolveRedHatFamilyDockerDistro(DistroInfo distro)
    {
        var id = distro.Id.ToLowerInvariant();
        if (id is "fedora" or "rhel" or "centos") return id;
        var likes = (distro.IdLike ?? string.Empty).ToLowerInvariant();
        if (likes.Contains("fedora")) return "fedora";
        if (likes.Contains("rhel") || likes.Contains("centos") || likes.Contains("rocky") || likes.Contains("almalinux"))
        {
            return "rhel";
        }
        return "rhel";
    }

    private static async Task<string> ResolveDpkgArchAsync(CancellationToken ct)
    {
        try
        {
            var result = await ProcessRunner.RunAsync("dpkg", ["--print-architecture"], ct: ct).ConfigureAwait(false);
            if (result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.StdOut))
            {
                return result.StdOut.Trim();
            }
        }
        catch
        {
            // dpkg is part of debian/ubuntu base - if it's missing we have bigger problems, but
            // fall back to a sane default so the install can still proceed on x86_64 hosts.
        }
        // 99%+ of self-host targets are x86_64 / amd64; arm64 operators can re-run after manually
        // dropping a `/etc/apt/sources.list.d/docker.list` for their arch.
        return "amd64";
    }

    private static async Task DownloadFileAsync(string url, string destinationPath, CancellationToken ct)
    {
        // 30s per file is generous - the GPG key is ~2 KB and the .repo file is ~200 B. The
        // explicit timeout means a broken network surfaces quickly instead of hanging the whole
        // prereqs phase.
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("interfold-bootstrap/1.0");

        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"failed to download {url}: HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        await using var fs = File.Create(destinationPath);
        await response.Content.CopyToAsync(fs, ct).ConfigureAwait(false);
    }

    private static async Task EnsureOpenSslAsync(DistroInfo distro, PhaseLogger logger, CancellationToken ct)
    {
        if (await ProcessRunner.ExistsOnPathAsync("openssl", ct).ConfigureAwait(false))
        {
            // openssl ships on basically every distro by default; nice to confirm anyway.
            return;
        }

        logger.Info("    installing openssl...");
        switch (distro.Family)
        {
            case DistroFamily.Debian:
                await RunAptInstallAsync(["openssl"], logger, ct).ConfigureAwait(false);
                break;
            case DistroFamily.RedHat:
                await RunDnfInstallAsync(["openssl"], logger, ct).ConfigureAwait(false);
                break;
        }
    }

    /// <summary>
    /// Distro-family-aware apt/dnf install seam reused by
    /// <see cref="ConfigPhase.ApplyPreFillMdnsCheckAsync"/> and
    /// <see cref="ConfigPhase.ApplyMdnsGateAsync"/> when the operator opts into installing
    /// avahi + nss-mdns from the mDNS banner / gate prompt. Dispatches to the existing
    /// private <see cref="RunAptInstallAsync"/> / <see cref="RunDnfInstallAsync"/> helpers
    /// so both callers speak the same DEBIAN_FRONTEND / <c>-y</c> shape and the "installer
    /// exited non-zero" error surfaces identically no matter who invoked it.
    /// </summary>
    /// <remarks>
    /// Kept internal (not public) so this only widens the phase's surface to sibling code in
    /// the bootstrapper assembly. The bootstrapper's existing prereqs flow keeps calling the
    /// private helpers directly — no behavioural change to the primary prereqs path.
    /// Non-Linux hosts throw: mDNS install only makes sense on the Linux families the
    /// bootstrapper otherwise supports, and short-circuiting there earlier is the caller's
    /// job (both <see cref="MdnsAvailability.IsHostnameResolvableAsync"/> and the
    /// <see cref="MdnsAvailability.InstallPackages"/> lookup return null / empty on
    /// unknown-family so we never reach this method with an unknown distro).
    /// </remarks>
    internal static async Task RunInstallAsync(
        DistroInfo distro,
        IEnumerable<string> packages,
        PhaseLogger logger,
        CancellationToken ct)
    {
        switch (distro.Family)
        {
            case DistroFamily.Debian:
                await RunAptInstallAsync(packages, logger, ct).ConfigureAwait(false);
                break;
            case DistroFamily.RedHat:
                await RunDnfInstallAsync(packages, logger, ct).ConfigureAwait(false);
                break;
            default:
                throw new InvalidOperationException(
                    $"Cannot install packages on unsupported distro family {distro.Family}. " +
                    $"Manual install required. Distro: {distro.PrettyName ?? distro.Id}.");
        }
    }

    private static async Task RunAptInstallAsync(IEnumerable<string> packages, PhaseLogger logger, CancellationToken ct)
    {
        var env = new Dictionary<string, string?> { ["DEBIAN_FRONTEND"] = "noninteractive" };
        var update = await ProcessRunner.RunAsync("apt-get", ["update"], environment: env, ct: ct).ConfigureAwait(false);
        if (update.ExitCode != 0)
        {
            logger.Warn($"apt-get update exited {update.ExitCode}: {update.StdErr.Trim()}");
        }
        var install = await ProcessRunner.RunAsync("apt-get",
            ["install", "-y", .. packages], environment: env, ct: ct).ConfigureAwait(false);
        if (install.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"apt-get install failed (exit {install.ExitCode}):\n{install.StdErr.Trim()}");
        }
    }

    private static async Task RunDnfInstallAsync(IEnumerable<string> packages, PhaseLogger logger, CancellationToken ct)
    {
        var install = await ProcessRunner.RunAsync("dnf", ["install", "-y", .. packages], ct: ct).ConfigureAwait(false);
        if (install.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"dnf install failed (exit {install.ExitCode}):\n{install.StdErr.Trim()}");
        }
    }

    private static async Task EnsureAioLimitAsync(int scyllaNodes, PhaseLogger logger, CancellationToken ct)
    {
        if (!File.Exists(AioSysctlPath))
        {
            logger.Warn($"{AioSysctlPath} not present; skipping AIO tuning (likely running in a constrained container).");
            return;
        }

        // Cassandra-only deployments (scyllaNodes==0) and any pre-existing operator override of
        // aio-max-nr both leave us with nothing to do, but we still take the chance to persist
        // the current value into the sysctl drop-in so reboots don't silently regress.
        var minRequired = scyllaNodes * AioPerNodeMin + AioHeadroom;
        var target = scyllaNodes * AioPerNodeRecommended + AioHeadroom;

        var current = int.Parse((await File.ReadAllTextAsync(AioSysctlPath, ct).ConfigureAwait(false)).Trim());
        if (current >= minRequired)
        {
            logger.Info($"    fs.aio-max-nr={current} (>= {minRequired} for {scyllaNodes} Scylla node(s)); ok");
            await PersistSysctlAsync(Math.Max(current, target), logger, ct).ConfigureAwait(false);
            return;
        }

        logger.Info($"    fs.aio-max-nr={current} (< {minRequired} for {scyllaNodes} Scylla node(s)); raising to {target}");
        try
        {
            await File.WriteAllTextAsync(AioSysctlPath, target.ToString(), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to raise fs.aio-max-nr to {target}: {ex.Message}. " +
                $"Set it manually with `sudo sysctl -w fs.aio-max-nr={target}` and re-run.", ex);
        }
        await PersistSysctlAsync(target, logger, ct).ConfigureAwait(false);
    }

    private static async Task PersistSysctlAsync(int value, PhaseLogger logger, CancellationToken ct)
    {
        // Make the AIO tuning survive a reboot via the standard sysctl.d drop-in dir.
        var content = $"# Interfold bootstrapper - required by Scylla/Seastar.\nfs.aio-max-nr = {value}\n";
        try
        {
            await File.WriteAllTextAsync(SysctlDropIn, content, ct).ConfigureAwait(false);
            logger.Info($"    persisted to {SysctlDropIn}");
        }
        catch (Exception ex)
        {
            logger.Warn($"could not write {SysctlDropIn}: {ex.Message} (setting is in effect for this boot)");
        }
    }
}
