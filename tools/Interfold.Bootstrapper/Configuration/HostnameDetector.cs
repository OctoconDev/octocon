using System.Text.RegularExpressions;

namespace Interfold.Bootstrapper.Configuration;

/// <summary>
/// Thin sibling of <see cref="LocalAddressDetector"/> that resolves the device's short hostname
/// into the mDNS-qualified <c>{hostname}.local</c> form used by the interactive
/// <see cref="Phases.ConfigPhase"/> "Public host(s)" pre-fill and by the pre-prompt mDNS banner.
///
/// <para>
/// The live entry point (<see cref="TryDetectMdnsHostname"/>) shells out to
/// <see cref="System.Net.Dns.GetHostName"/> and defers the actual filtering rules to
/// <see cref="QualifyForMdns"/> — a pure static helper unit tests can drive directly without
/// touching the OS. Rejection cases the qualifier catches:
/// <list type="bullet">
///   <item>Null / empty / whitespace input — no hostname at all.</item>
///   <item><c>localhost</c> (any casing) — a mDNS advertisement here would be a lie: the name
///         resolves to loopback, not the LAN address the bootstrapper is issuing certs for.</item>
///   <item>Already-qualified FQDNs (contain <c>.</c>) — appending <c>.local</c> to
///         <c>host.corp.example.com</c> produces a bogus double-suffixed name. Only bare short
///         labels get the <c>.local</c> suffix.</item>
///   <item>Invalid DNS label characters or lengths outside RFC 1035's 1..63 byte label limit —
///         the resulting <c>{name}.local</c> wouldn't parse as a DNS name downstream anyway.</item>
/// </list>
/// </para>
/// </summary>
internal static class HostnameDetector
{
    /// <summary>
    /// RFC 1035-shaped label check: 1..63 characters, ASCII letters/digits/hyphens, must start
    /// with a letter or digit (no leading hyphen). Deliberately permissive on trailing hyphens
    /// — the DNS grammar technically forbids them too, but real-world hostnames occasionally
    /// have them and rejecting here would drop the hostname from the pre-fill for no
    /// operator-facing benefit.
    /// </summary>
    private static readonly Regex ShortHostnamePattern =
        new("^[A-Za-z0-9][A-Za-z0-9-]{0,62}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Live device-hostname probe. Reads <see cref="System.Net.Dns.GetHostName"/> (which on
    /// every supported OS returns the kernel's short hostname) and defers the filtering rules
    /// to <see cref="QualifyForMdns"/>. Any exception is swallowed and reported as null so a
    /// platform surprise degrades to "no hostname pre-fill" rather than crashing the bootstrap.
    /// </summary>
    public static string? TryDetectMdnsHostname()
    {
        try
        {
            var raw = System.Net.Dns.GetHostName();
            return QualifyForMdns(raw);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Pure filter used by <see cref="TryDetectMdnsHostname"/> and directly exercised by unit
    /// tests. Applies the rejection rules documented on the class summary and returns
    /// <c>{name}.local</c> when the input passes. Trims surrounding whitespace before matching
    /// so a hostname that survived a copy/paste with an accidental trailing newline still
    /// qualifies.
    /// </summary>
    public static string? QualifyForMdns(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }
        var name = raw.Trim();
        if (string.Equals(name, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        // Reject already-qualified names — appending .local to `host.corp.example.com` would
        // produce a bogus mDNS name. Only bare short hostnames get the .local suffix.
        if (name.Contains('.', StringComparison.Ordinal))
        {
            return null;
        }
        if (!ShortHostnamePattern.IsMatch(name))
        {
            return null;
        }
        return $"{name}.local";
    }
}
