using System.Net;
using System.Net.Sockets;
using Interfold.Bootstrapper.Configuration;

namespace Interfold.Bootstrapper.UnitTests;

/// <summary>
/// Unit tests for <see cref="HostParser"/>. Pins the precedence rules (CIDR before IP literal
/// before DNS), the operator-actionable rejection messages, and the byte-layout the cert phase
/// downstream depends on for iPAddress permittedSubtree encoding.
/// </summary>
public sealed class HostParserTests
{
    /// <summary>
    /// Invokes <see cref="HostParser.Parse"/> with the supplied bad input and asserts the resulting
    /// <see cref="FormatException"/>'s message contains every supplied fragment. Collapses the
    /// arrange-throws-assert triple every negative test here would otherwise re-write.
    /// </summary>
    private static async Task AssertHostParseFailsAsync(string input, params string[] messageFragments)
    {
        var ex = Assert.Throws<FormatException>(() => HostParser.Parse(input));
        foreach (var frag in messageFragments)
        {
            await Assert.That(ex.Message).Contains(frag);
        }
    }

    [Test]
    public async Task DnsNameClassifiesAsDns()
    {
        var entry = HostParser.Parse("api.example.com");
        await Assert.That(entry.Kind).IsEqualTo(HostKind.Dns);
        await Assert.That(entry.DnsName).IsEqualTo("api.example.com");
        await Assert.That(entry.Ip).IsNull();
        await Assert.That(entry.IsLeafEligible).IsTrue();
        await Assert.That(entry.IsCidr).IsFalse();
    }

    [Test]
    public async Task WildcardDnsNameClassifiesAsDns()
    {
        // Wildcards are valid leaf-SAN dNSName values and produce a suffix-only Name Constraints
        // subtree (handled in CertificatePhase.StripWildcardPrefix). HostParser keeps the raw form.
        var entry = HostParser.Parse("*.example.com");
        await Assert.That(entry.Kind).IsEqualTo(HostKind.Dns);
        await Assert.That(entry.DnsName).IsEqualTo("*.example.com");
    }

    [Test]
    public async Task Ipv4LiteralClassifiesAsIpv4()
    {
        var entry = HostParser.Parse("192.168.1.42");
        await Assert.That(entry.Kind).IsEqualTo(HostKind.Ipv4);
        await Assert.That(entry.Ip).IsNotNull();
        await Assert.That(entry.Ip!.AddressFamily).IsEqualTo(AddressFamily.InterNetwork);
        await Assert.That(entry.PrefixLength).IsEqualTo(32);
        await Assert.That(entry.IsLeafEligible).IsTrue();
    }

    [Test]
    public async Task Ipv6LiteralClassifiesAsIpv6()
    {
        var entry = HostParser.Parse("fe80::1234");
        await Assert.That(entry.Kind).IsEqualTo(HostKind.Ipv6);
        await Assert.That(entry.Ip!.AddressFamily).IsEqualTo(AddressFamily.InterNetworkV6);
        await Assert.That(entry.PrefixLength).IsEqualTo(128);
    }

    [Test]
    public async Task BracketedIpv6LiteralIsAccepted()
    {
        // Operators pasting from a URL ("https://[::1]") shouldn't have to strip the brackets
        // manually. The parser handles either form and stores the canonical unbracketed IPAddress.
        var entry = HostParser.Parse("[::1]");
        await Assert.That(entry.Kind).IsEqualTo(HostKind.Ipv6);
        await Assert.That(entry.Ip!.ToString()).IsEqualTo("::1");
    }

    [Test]
    public async Task Ipv4CidrClassifiesAsIpv4Cidr()
    {
        var entry = HostParser.Parse("192.168.1.0/24");
        await Assert.That(entry.Kind).IsEqualTo(HostKind.Ipv4Cidr);
        await Assert.That(entry.Ip!.AddressFamily).IsEqualTo(AddressFamily.InterNetwork);
        await Assert.That(entry.PrefixLength).IsEqualTo(24);
        await Assert.That(entry.IsCidr).IsTrue();
        await Assert.That(entry.IsLeafEligible).IsFalse();
    }

    [Test]
    public async Task Ipv6CidrClassifiesAsIpv6Cidr()
    {
        var entry = HostParser.Parse("fe80::/64");
        await Assert.That(entry.Kind).IsEqualTo(HostKind.Ipv6Cidr);
        await Assert.That(entry.PrefixLength).IsEqualTo(64);
    }

    [Test]
    public Task EmptyInputRejected()
        => AssertHostParseFailsAsync("   ", "empty");

    [Test]
    public Task DnsNameWithSpaceRejected()
        // Mirrors the previous Validate() RFC 1035-lite rule. Whitespace is always a typo, so we
        // reject before the cert phase tries to use it as a dNSName value.
        => AssertHostParseFailsAsync("api example.com", "whitespace");

    [Test]
    public Task DnsNameTooLongRejected()
        // 254 chars - one over the RFC 1035 limit. Use a single-label form so we don't trip on
        // any unrelated label-length check.
        => AssertHostParseFailsAsync(new string('a', 254), "253");

    [Test]
    public Task Ipv4CidrWithHostBitsSetRejectedWithFixIt()
        // RFC 5280 §4.2.1.10 requires host bits beyond the mask to be zero. Surface a fix-it that
        // shows both the network-mask interpretation and the single-host interpretation so the
        // operator can pick the one they meant.
        => AssertHostParseFailsAsync("192.168.1.42/24", "host bits", "192.168.1.0/24", "192.168.1.42/32");

    [Test]
    public Task Ipv4CidrPrefixOutOfRangeRejected()
        => AssertHostParseFailsAsync("10.0.0.0/33", "out of range", "32");

    [Test]
    public Task Ipv6CidrPrefixOutOfRangeRejected()
        => AssertHostParseFailsAsync("fe80::/129", "out of range", "128");

    [Test]
    public Task CidrWithNonNumericPrefixRejected()
        => AssertHostParseFailsAsync("10.0.0.0/abc", "non-negative integer");

    [Test]
    public async Task BuildNetmaskIpv4SlashTwentyFour()
    {
        // /24 = FF FF FF 00 — the canonical class-C mask, ubiquitous on LANs. If this is wrong,
        // every iPAddress permittedSubtree downstream is wrong.
        var mask = HostParser.BuildNetmask(byteLength: 4, prefix: 24);
        await Assert.That(mask).IsEquivalentTo(new byte[] { 0xFF, 0xFF, 0xFF, 0x00 });
    }

    [Test]
    public async Task BuildNetmaskIpv4FullHost()
    {
        // /32 = FF FF FF FF — the mask used when a single IPv4 literal becomes a Name Constraints
        // subtree (no CIDR specified by the operator).
        var mask = HostParser.BuildNetmask(byteLength: 4, prefix: 32);
        await Assert.That(mask).IsEquivalentTo(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF });
    }

    [Test]
    public async Task BuildNetmaskIpv4PartialByte()
    {
        // /20 = FF FF F0 00 — exercises the trailing-bits branch (prefix not on a byte boundary).
        var mask = HostParser.BuildNetmask(byteLength: 4, prefix: 20);
        await Assert.That(mask).IsEquivalentTo(new byte[] { 0xFF, 0xFF, 0xF0, 0x00 });
    }

    [Test]
    public async Task BuildNetmaskIpv6FullHost()
    {
        // /128 = sixteen 0xFF bytes — the mask used for a single IPv6 literal subtree.
        var mask = HostParser.BuildNetmask(byteLength: 16, prefix: 128);
        var expected = new byte[16];
        Array.Fill(expected, (byte)0xFF);
        await Assert.That(mask).IsEquivalentTo(expected);
    }

    [Test]
    public async Task ToNameConstraintSubtreeBytesIpv4Cidr()
    {
        // RFC 5280 §4.2.1.10: payload is address || mask. 192.168.1.0/24 → C0 A8 01 00 FF FF FF 00.
        var entry = HostParser.Parse("192.168.1.0/24");
        var bytes = HostParser.ToNameConstraintSubtreeBytes(entry);
        await Assert.That(bytes).IsEquivalentTo(new byte[]
        {
            0xC0, 0xA8, 0x01, 0x00,
            0xFF, 0xFF, 0xFF, 0x00,
        });
    }

    [Test]
    public async Task ToNameConstraintSubtreeBytesIpv4SingleHost()
    {
        // Bare IPv4 literal gets a /32 mask. 192.168.1.42 → C0 A8 01 2A FF FF FF FF.
        var entry = HostParser.Parse("192.168.1.42");
        var bytes = HostParser.ToNameConstraintSubtreeBytes(entry);
        await Assert.That(bytes).IsEquivalentTo(new byte[]
        {
            0xC0, 0xA8, 0x01, 0x2A,
            0xFF, 0xFF, 0xFF, 0xFF,
        });
    }

    [Test]
    public async Task ToNameConstraintSubtreeBytesIpv6SingleHost()
    {
        // ::1 with /128 mask → 32 bytes total (16 addr + 16 mask). Spot-check the boundary.
        var entry = HostParser.Parse("::1");
        var bytes = HostParser.ToNameConstraintSubtreeBytes(entry);
        await Assert.That(bytes.Length).IsEqualTo(32);
        await Assert.That(bytes[15]).IsEqualTo((byte)0x01);
        await Assert.That(bytes[16]).IsEqualTo((byte)0xFF);
        await Assert.That(bytes[31]).IsEqualTo((byte)0xFF);
    }

    [Test]
    public async Task ToUrlHostBracketsIpv6()
    {
        // RFC 3986 §3.2.2: a host that is an IPv6 literal MUST be enclosed in square brackets in
        // a URI. Used by ResolveDerivedDefaults so `https://[::1]` is the derived callback base.
        var entry = HostParser.Parse("::1");
        await Assert.That(HostParser.ToUrlHost(entry)).IsEqualTo("[::1]");
    }

    [Test]
    public async Task ToUrlHostLeavesIpv4Unbracketed()
    {
        var entry = HostParser.Parse("192.168.1.42");
        await Assert.That(HostParser.ToUrlHost(entry)).IsEqualTo("192.168.1.42");
    }

    [Test]
    public async Task ToUrlHostLeavesDnsUnbracketed()
    {
        var entry = HostParser.Parse("api.example.com");
        await Assert.That(HostParser.ToUrlHost(entry)).IsEqualTo("api.example.com");
    }

    [Test]
    public async Task ToUrlHostThrowsForCidr()
    {
        var entry = HostParser.Parse("10.0.0.0/8");
        Assert.Throws<InvalidOperationException>(() => HostParser.ToUrlHost(entry));
        await Task.CompletedTask;
    }

    [Test]
    public async Task PickPrimarySkipsCidr()
    {
        // CIDR entries appear first; PickPrimary must walk past them to the first leaf-eligible
        // entry. Matches the rule baked into ResolveDerivedDefaults / PublishPhase.PickServerName.
        var entries = new[]
        {
            HostParser.Parse("192.168.1.0/24"),
            HostParser.Parse("10.0.0.0/8"),
            HostParser.Parse("api.example.com"),
        };
        var primary = HostParser.PickPrimary(entries);
        await Assert.That(primary).IsNotNull();
        await Assert.That(primary!.Raw).IsEqualTo("api.example.com");
    }

    [Test]
    public async Task PickPrimaryReturnsNullWhenAllCidr()
    {
        var entries = new[]
        {
            HostParser.Parse("192.168.1.0/24"),
            HostParser.Parse("fe80::/64"),
        };
        await Assert.That(HostParser.PickPrimary(entries)).IsNull();
    }

    [Test]
    public async Task IsHostBitsZeroDetectsNonZero()
    {
        // 192.168.1.42 = C0 A8 01 2A. With /24 the host byte must be 00; 0x2A means host bits set.
        var addr = IPAddress.Parse("192.168.1.42").GetAddressBytes();
        await Assert.That(HostParser.IsHostBitsZero(addr, 24)).IsFalse();
    }

    [Test]
    public async Task IsHostBitsZeroAcceptsExactNetwork()
    {
        var addr = IPAddress.Parse("192.168.1.0").GetAddressBytes();
        await Assert.That(HostParser.IsHostBitsZero(addr, 24)).IsTrue();
    }
}
