using Interfold.Bootstrapper.Configuration;

namespace Interfold.Bootstrapper.UnitTests;

/// <summary>
/// Unit tests for <see cref="HostnameDetector"/>. The live entry point
/// <see cref="HostnameDetector.TryDetectMdnsHostname"/> calls
/// <see cref="System.Net.Dns.GetHostName"/> (non-mockable), so the deterministic surface is the
/// pure <see cref="HostnameDetector.QualifyForMdns"/> static helper — same shape as
/// <c>LocalAddressDetectorTests</c>' <c>Pick</c> / <c>IsVirtualName</c> split.
///
/// Coverage below pins every rejection branch of the qualifier plus the happy path so a
/// regression that (say) drops the FQDN or invalid-chars filter fails a specific test rather
/// than showing up as a mysterious pre-fill on a downstream integration suite.
/// </summary>
public sealed class HostnameDetectorTests
{
    [Test]
    public async Task QualifyForMdnsRejectsNull()
    {
        // Guards the null-safe null-input path — HostnameDetector.TryDetectMdnsHostname wraps
        // Dns.GetHostName in try/catch and returns null on platform surprise; the pure qualifier
        // needs to survive that null itself without throwing.
        var result = HostnameDetector.QualifyForMdns(null);
        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task QualifyForMdnsRejectsEmpty()
    {
        var result = HostnameDetector.QualifyForMdns(string.Empty);
        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task QualifyForMdnsRejectsWhitespace()
    {
        // "   " is neither an empty string nor a valid hostname — the whitespace check has to
        // catch it before the trim + regex match path would otherwise reject on regex-only.
        var result = HostnameDetector.QualifyForMdns("   ");
        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task QualifyForMdnsRejectsLocalhost()
    {
        // "localhost" resolves to loopback; advertising loopback via mDNS would be a lie. The
        // check is case-insensitive so "Localhost" / "LOCALHOST" are also rejected.
        await Assert.That(HostnameDetector.QualifyForMdns("localhost")).IsNull();
        await Assert.That(HostnameDetector.QualifyForMdns("Localhost")).IsNull();
        await Assert.That(HostnameDetector.QualifyForMdns("LOCALHOST")).IsNull();
    }

    [Test]
    public async Task QualifyForMdnsRejectsFqdn()
    {
        // Already-qualified names (any dot) get rejected — appending ".local" to
        // "host.corp.example.com" would produce a bogus double-suffixed name. This also
        // catches the case where the operator has already put ".local" on their hostname
        // manually (Dns.GetHostName() sometimes returns that on macOS).
        await Assert.That(HostnameDetector.QualifyForMdns("host.corp.example.com")).IsNull();
        await Assert.That(HostnameDetector.QualifyForMdns("workstation.local")).IsNull();
        await Assert.That(HostnameDetector.QualifyForMdns("a.b")).IsNull();
    }

    [Test]
    public async Task QualifyForMdnsRejectsInvalidCharacters()
    {
        // The RFC 1035 label check rejects punctuation, whitespace, and leading hyphens. A
        // fabricated hostname with any of these would produce a bogus mDNS name downstream
        // (the resulting {name}.local wouldn't parse via HostParser either).
        await Assert.That(HostnameDetector.QualifyForMdns("host!name")).IsNull();
        await Assert.That(HostnameDetector.QualifyForMdns("host name")).IsNull();
        await Assert.That(HostnameDetector.QualifyForMdns("host_name")).IsNull();
        await Assert.That(HostnameDetector.QualifyForMdns("-startdash")).IsNull();
    }

    [Test]
    public async Task QualifyForMdnsRejectsTooLongLabel()
    {
        // RFC 1035 caps DNS labels at 63 characters — anything longer would produce an invalid
        // {name}.local that HostParser.Parse would then reject downstream. Filtering here means
        // the caller (the pre-fill banner) gets a clean "no hostname" signal rather than a
        // hostname that later fails validation.
        var tooLong = new string('a', 64);
        await Assert.That(HostnameDetector.QualifyForMdns(tooLong)).IsNull();
    }

    [Test]
    public async Task QualifyForMdnsAppendsLocalSuffixToBareHostname()
    {
        // The happy path — a plain short hostname gets ".local" appended and returned as the
        // mDNS-qualified name the pre-fill banner will offer to the operator.
        var result = HostnameDetector.QualifyForMdns("workstation");
        await Assert.That(result).IsEqualTo("workstation.local");
    }

    [Test]
    public async Task QualifyForMdnsAcceptsHostnameWithDigitsAndHyphens()
    {
        // The RFC 1035 label grammar allows digits and hyphens as long as the first character
        // isn't a hyphen. Common in real-world hostnames ("dev-box-01", "node2"); rejecting
        // these would drop the pre-fill for entire categories of legitimate operator setups.
        await Assert.That(HostnameDetector.QualifyForMdns("dev-box-01")).IsEqualTo("dev-box-01.local");
        await Assert.That(HostnameDetector.QualifyForMdns("node2")).IsEqualTo("node2.local");
    }

    [Test]
    public async Task QualifyForMdnsTrimsSurroundingWhitespace()
    {
        // A hostname that survived a copy/paste with a trailing newline or leading space
        // should still qualify — the trim runs before the label check so the resulting
        // {name}.local is clean.
        await Assert.That(HostnameDetector.QualifyForMdns("  workstation  ")).IsEqualTo("workstation.local");
        await Assert.That(HostnameDetector.QualifyForMdns("workstation\n")).IsEqualTo("workstation.local");
    }

    [Test]
    public async Task QualifyForMdnsAccepts63CharLabel()
    {
        // 63 characters is the exact RFC 1035 label limit — the boundary case worth pinning
        // because an off-by-one in the regex (e.g. {0,61} instead of {0,62}) would silently
        // reject valid hostnames the qualifier is supposed to admit.
        var maxLabel = new string('a', 63);
        var result = HostnameDetector.QualifyForMdns(maxLabel);
        await Assert.That(result).IsEqualTo($"{maxLabel}.local");
    }

    [Test]
    public async Task LiveDetectorRunsWithoutThrowing()
    {
        // Smoke test for the live path: TryDetectMdnsHostname must never throw. Any platform
        // surprise (Dns.GetHostName failure, permissions issue) is swallowed and reported as
        // null. We don't assert WHAT it returns (depends on the runner's hostname), only that
        // the call completes and produces either null or a value ending in ".local".
        var result = HostnameDetector.TryDetectMdnsHostname();
        if (result is not null)
        {
            await Assert.That(result).EndsWith(".local");
        }
    }
}
