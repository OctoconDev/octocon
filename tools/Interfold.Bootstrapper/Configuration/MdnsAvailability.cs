using System.Runtime.InteropServices;
using Interfold.Bootstrapper.Cli;
using Interfold.Bootstrapper.Phases;
using Interfold.Bootstrapper.Util;

namespace Interfold.Bootstrapper.Configuration;

/// <summary>
/// Detects whether the current device can actually resolve mDNS-shaped (<c>*.local</c>)
/// hostnames on the LAN, plus package-recipe helpers for the interactive install prompt
/// used by <see cref="Phases.ConfigPhase"/>'s pre-prompt banner and post-fill safety gate.
///
/// <para>
/// Lives in <see cref="Configuration"/> (not <see cref="Phases"/>) because it's a pure
/// detection / side-effect utility invoked from <see cref="Phases.ConfigPhase"/> — mirrors
/// the placement of <see cref="LocalAddressDetector"/>, which is the same shape.
/// </para>
/// </summary>
internal static class MdnsAvailability
{
    /// <summary>
    /// Shells out to <c>getent hosts &lt;name&gt;</c> — exits 0 iff the whole
    /// nsswitch → mdns_minimal → avahi chain successfully resolves <paramref name="hostname"/>.
    /// The exit-code + stdout check together beat probing avahi's systemd unit status because
    /// a running daemon with a broken <c>nsswitch.conf</c> would still fail resolution.
    ///
    /// <para>
    /// Returns <c>null</c> on non-Linux platforms (getent doesn't exist on Windows / macOS in
    /// the shape the bootstrapper needs), which the callers treat as "unknown — skip the
    /// gate". The bootstrap phase itself is Linux-only anyway; this branch just lets the
    /// ConfigPhase logic run cleanly on a dev workstation for JSON validation.
    /// </para>
    /// </summary>
    public static async Task<bool?> IsHostnameResolvableAsync(string hostname, CancellationToken ct)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return null;
        }

        try
        {
            var run = await ProcessRunner.RunAsync("getent", ["hosts", hostname], ct: ct).ConfigureAwait(false);
            return run.ExitCode == 0 && !string.IsNullOrWhiteSpace(run.StdOut);
        }
        catch
        {
            // getent binary missing or Process.Start failure — treat as "cannot probe" so the
            // gate short-circuits into skip rather than mutating the operator's hosts list on
            // an environmental issue we can't diagnose.
            return null;
        }
    }

    /// <summary>
    /// Distro-family-specific package list to satisfy the mDNS resolution chain:
    /// <c>avahi-daemon</c> (the daemon itself) plus the platform-appropriate NSS module
    /// (<c>libnss-mdns</c> on Debian/Ubuntu, <c>nss-mdns</c> on Fedora/RHEL). Empty list for
    /// unknown / unsupported families — the caller falls back to the manual install hint
    /// instead of attempting an install.
    /// </summary>
    public static IReadOnlyList<string> InstallPackages(DistroFamily family) => family switch
    {
        DistroFamily.Debian => ["avahi-daemon", "libnss-mdns"],
        DistroFamily.RedHat => ["avahi", "nss-mdns"],
        _ => [],
    };

    /// <summary>
    /// Copy-pasteable one-liner that gets an operator to a working mDNS stack from a clean
    /// distro. Included in the "mDNS unavailable" warning both banners emit so the operator
    /// can act on it without leaving the terminal. The <c>systemctl enable --now</c> tail is
    /// present in the shell recipe because the packages don't self-enable the daemon on every
    /// distro (Debian starts it via a maintainer script; RHEL leaves it inactive).
    /// </summary>
    public static string ManualInstallHint(DistroFamily family) => family switch
    {
        DistroFamily.Debian =>
            "sudo apt-get install -y avahi-daemon libnss-mdns && sudo systemctl enable --now avahi-daemon",
        DistroFamily.RedHat =>
            "sudo dnf install -y avahi nss-mdns && sudo systemctl enable --now avahi-daemon",
        _ =>
            "(install avahi-daemon and its nss module for your distro, then enable the service)",
    };

    /// <summary>
    /// Attempts to install the avahi packages for <paramref name="distro"/> and enable the
    /// daemon via <c>systemctl</c>. Returns <c>true</c> when the install succeeded (the
    /// caller should re-probe to confirm resolution works end-to-end); <c>false</c> when
    /// the distro is unsupported or the install failed (details are logged).
    /// </summary>
    public static async Task<bool> TryInstallAvahiAsync(
        DistroInfo distro, PhaseLogger logger, CancellationToken ct)
    {
        var packages = InstallPackages(distro.Family);
        if (packages.Count == 0)
        {
            logger.Warn($"    unsupported distro family {distro.Family}; skipping avahi install");
            return false;
        }

        logger.Info($"    installing avahi ({string.Join(" ", packages)}) ...");
        try
        {
            await PrerequisitesPhase.RunInstallAsync(distro, packages, logger, ct).ConfigureAwait(false);
            try
            {
                await ProcessRunner.RunAsync(
                    "systemctl", ["enable", "--now", "avahi-daemon"], ct: ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.Warn($"    could not `systemctl enable --now avahi-daemon` ({ex.GetType().Name}: {ex.Message}); " +
                            "start it manually if the re-probe below still fails.");
            }
            return true;
        }
        catch (Exception ex)
        {
            logger.Warn($"    avahi install failed ({ex.GetType().Name}: {ex.Message})");
            return false;
        }
    }
}
