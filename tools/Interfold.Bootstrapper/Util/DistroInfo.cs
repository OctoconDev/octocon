namespace Interfold.Bootstrapper.Util;

/// <summary>
/// Identifies the Linux distribution family. Drives apt-vs-dnf selection and the trust-store path.
/// </summary>
public enum DistroFamily
{
    Unknown,
    Debian,
    RedHat,
}

/// <summary>
/// Subset of <c>/etc/os-release</c> we care about. See
/// <a href="https://www.freedesktop.org/software/systemd/man/os-release.html">os-release(5)</a>.
/// </summary>
/// <param name="Id">The lower-case OS identifier, e.g. <c>ubuntu</c>, <c>debian</c>, <c>fedora</c>.</param>
/// <param name="IdLike">Optional space-separated downstream chain (e.g. Linux Mint sets this to <c>ubuntu debian</c>).</param>
/// <param name="PrettyName">Human-readable name, e.g. <c>Ubuntu 24.04 LTS</c>.</param>
/// <param name="VersionId">Numeric version, e.g. <c>24.04</c> for Ubuntu, <c>40</c> for Fedora.</param>
/// <param name="VersionCodename">Debian/Ubuntu codename (<c>noble</c>, <c>bookworm</c>); null on non-Debian distros.</param>
/// <param name="Family">Resolved family classification used to pick apt vs dnf.</param>
public sealed record DistroInfo(
    string Id,
    string? IdLike,
    string? PrettyName,
    string? VersionId,
    string? VersionCodename,
    DistroFamily Family)
{
    private const string OsReleasePath = "/etc/os-release";

    public static DistroInfo Read()
    {
        if (!File.Exists(OsReleasePath))
        {
            return new DistroInfo(
                Id: "unknown",
                IdLike: null,
                PrettyName: null,
                VersionId: null,
                VersionCodename: null,
                Family: DistroFamily.Unknown);
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in File.ReadAllLines(OsReleasePath))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim().Trim('"');
            values[key] = value;
        }

        values.TryGetValue("ID", out var id);
        values.TryGetValue("ID_LIKE", out var idLike);
        values.TryGetValue("PRETTY_NAME", out var prettyName);
        values.TryGetValue("VERSION_ID", out var versionId);
        // VERSION_CODENAME is the canonical codename; UBUNTU_CODENAME is the ubuntu-base used by
        // downstream debian-likes (Mint, Pop!_OS) when their VERSION_CODENAME is their own name.
        if (!values.TryGetValue("VERSION_CODENAME", out var versionCodename))
        {
            values.TryGetValue("UBUNTU_CODENAME", out versionCodename);
        }

        var family = Resolve(id, idLike);
        return new DistroInfo(id ?? "unknown", idLike, prettyName, versionId, versionCodename, family);
    }

    private static DistroFamily Resolve(string? id, string? idLike)
    {
        // ID alone is enough for the canonical distros; ID_LIKE catches downstreams (Mint -> ubuntu, Rocky -> rhel).
        var haystack = string.Join(' ', id ?? string.Empty, idLike ?? string.Empty).ToLowerInvariant();

        if (haystack.Contains("debian") || haystack.Contains("ubuntu"))
        {
            return DistroFamily.Debian;
        }
        if (haystack.Contains("rhel") || haystack.Contains("fedora") || haystack.Contains("centos") || haystack.Contains("rocky") || haystack.Contains("almalinux"))
        {
            return DistroFamily.RedHat;
        }
        return DistroFamily.Unknown;
    }
}
