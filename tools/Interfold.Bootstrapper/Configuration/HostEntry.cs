using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Interfold.Bootstrapper.Configuration;

/// <summary>Shape of one <see cref="DeploymentSection.Hosts"/> entry. Dns → dNSName SAN +
/// Name Constraints subtree. Ipv4/Ipv6 → iPAddress SAN + subtree with all-ones mask. CIDRs
/// → subtree only; excluded from the leaf SAN (leaf serves a single host) and from primary-host
/// derivation (no canonical URL form for a network range).</summary>
internal enum HostKind
{
    Dns,
    Ipv4,
    Ipv6,
    Ipv4Cidr,
    Ipv6Cidr,
}

/// <summary>Parsed host entry. <see cref="Raw"/> preserves operator input for error
/// messages. <see cref="PrefixLength"/> is 32/128 for single-IP kinds; the operator prefix
/// for CIDR.</summary>
internal sealed record HostEntry(
    string Raw,
    HostKind Kind,
    string? DnsName,
    IPAddress? Ip,
    int PrefixLength)
{
    public bool IsCidr => HostParser.IsCidr(Kind);
    public bool IsLeafEligible => HostParser.IsLeafEligible(Kind);
}

/// <summary>Authoritative host classifier. Callers pattern-match on <see cref="HostKind"/>
/// instead of re-parsing the raw string.</summary>
internal static class HostParser
{
    public const int Ipv4Bits = 32;
    public const int Ipv6Bits = 128;

    public static bool IsCidr(HostKind k) => k is HostKind.Ipv4Cidr or HostKind.Ipv6Cidr;

    /// <summary>Non-CIDR kinds contribute a leaf-cert SAN and are eligible as the primary host.</summary>
    public static bool IsLeafEligible(HostKind k) => k is HostKind.Dns or HostKind.Ipv4 or HostKind.Ipv6;

    /// <summary>Precedence: slash → CIDR; else IP literal; else DNS (RFC 1035-ish check).
    /// Bracketed IPv6 literals (<c>[::1]</c>) accepted; CIDR must use unbracketed
    /// (<c>fe80::/64</c>). Throws <see cref="FormatException"/> with an operator-actionable
    /// message on malformed input.</summary>
    public static HostEntry Parse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new FormatException("host entry must not be empty or whitespace");
        }
        var trimmed = raw.Trim();

        var slashIndex = trimmed.IndexOf('/');
        if (slashIndex >= 0)
        {
            return ParseCidr(trimmed, slashIndex);
        }

        var addrCandidate = trimmed.Length >= 2 && trimmed[0] == '[' && trimmed[^1] == ']'
            ? trimmed[1..^1]
            : trimmed;
        if (IPAddress.TryParse(addrCandidate, out var ip))
        {
            return ip.AddressFamily switch
            {
                AddressFamily.InterNetwork => new HostEntry(trimmed, HostKind.Ipv4, null, ip, Ipv4Bits),
                AddressFamily.InterNetworkV6 => new HostEntry(trimmed, HostKind.Ipv6, null, ip, Ipv6Bits),
                _ => throw new FormatException($"'{trimmed}' parsed as an IP address of unsupported family '{ip.AddressFamily}'"),
            };
        }

        ValidateDnsName(trimmed);
        return new HostEntry(trimmed, HostKind.Dns, trimmed, null, 0);
    }

    private static HostEntry ParseCidr(string trimmed, int slashIndex)
    {
        var addrText = trimmed[..slashIndex];
        var lenText = trimmed[(slashIndex + 1)..];

        if (!IPAddress.TryParse(addrText, out var ip))
        {
            throw new FormatException($"CIDR '{trimmed}': '{addrText}' is not a valid IP literal");
        }
        if (!int.TryParse(lenText, NumberStyles.None, CultureInfo.InvariantCulture, out var prefix))
        {
            throw new FormatException($"CIDR '{trimmed}': prefix '{lenText}' is not a non-negative integer");
        }

        var maxPrefix = ip.AddressFamily switch
        {
            AddressFamily.InterNetwork => Ipv4Bits,
            AddressFamily.InterNetworkV6 => Ipv6Bits,
            _ => throw new FormatException($"CIDR '{trimmed}': unsupported IP family '{ip.AddressFamily}'"),
        };
        if (prefix < 0 || prefix > maxPrefix)
        {
            throw new FormatException($"CIDR '{trimmed}': prefix /{prefix} out of range, must be between 0 and {maxPrefix}");
        }

        var addr = ip.GetAddressBytes();
        if (!IsHostBitsZero(addr, prefix))
        {
            // RFC 5280 §4.2.1.10: host bits beyond the mask MUST be zero. Fix-it rather than
            // silently snap — "192.168.1.42/24" usually means /32, not the whole /24.
            var canonical = new IPAddress(MaskAddress(addr, prefix)).ToString();
            var singleHost = $"{ip}/{maxPrefix}";
            throw new FormatException(
                $"CIDR '{trimmed}' has host bits set beyond the /{prefix} mask. " +
                $"Use '{canonical}/{prefix}' to pin the network, or '{singleHost}' to pin a single host.");
        }

        var kind = ip.AddressFamily == AddressFamily.InterNetwork ? HostKind.Ipv4Cidr : HostKind.Ipv6Cidr;
        return new HostEntry(trimmed, kind, null, ip, prefix);
    }

    private static void ValidateDnsName(string name)
    {
        // Enough to reject typos (spaces, over-long); IP-literal parsing already ran upstream.
        var probe = name.StartsWith("*.", StringComparison.Ordinal) ? name[2..] : name;
        if (probe.Length == 0)
        {
            throw new FormatException($"'{name}' is not a valid host (DNS name, IP literal, or CIDR)");
        }
        if (name.Length > 253)
        {
            throw new FormatException($"DNS name '{name}' exceeds 253 characters");
        }
        if (name.AsSpan().ContainsAny(' ', '\t'))
        {
            throw new FormatException($"DNS name '{name}' contains whitespace");
        }
    }

    /// <summary>Network-byte-order netmask. <paramref name="byteLength"/> is the address-byte
    /// count (4 for IPv4, 16 for IPv6); <paramref name="prefix"/> the bit count to set.</summary>
    public static byte[] BuildNetmask(int byteLength, int prefix)
    {
        if (prefix < 0 || prefix > byteLength * 8)
        {
            throw new ArgumentOutOfRangeException(nameof(prefix));
        }
        var result = new byte[byteLength];
        var fullBytes = prefix / 8;
        for (var i = 0; i < fullBytes; i++)
        {
            result[i] = 0xFF;
        }
        var trailingBits = prefix - (fullBytes * 8);
        if (trailingBits > 0 && fullBytes < byteLength)
        {
            result[fullBytes] = (byte)(0xFF << (8 - trailingBits));
        }
        return result;
    }

    /// <summary>RFC 5280 §4.2.1.10 "host bits MUST be zero" check for iPAddress subtrees.</summary>
    public static bool IsHostBitsZero(byte[] addr, int prefix)
    {
        var mask = BuildNetmask(addr.Length, prefix);
        for (var i = 0; i < addr.Length; i++)
        {
            if ((addr[i] & (byte)~mask[i]) != 0)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Address ANDed with the netmask — used to render the canonical network
    /// address in a host-bits-set fix-it.</summary>
    public static byte[] MaskAddress(byte[] addr, int prefix)
    {
        var mask = BuildNetmask(addr.Length, prefix);
        var result = new byte[addr.Length];
        for (var i = 0; i < addr.Length; i++)
        {
            result[i] = (byte)(addr[i] & mask[i]);
        }
        return result;
    }

    /// <summary>iPAddress GeneralName payload: <c>address || mask</c> per RFC 5280 §4.2.1.10
    /// (8 octets IPv4, 32 IPv6). Throws on non-IP kinds.</summary>
    public static byte[] ToNameConstraintSubtreeBytes(HostEntry entry)
    {
        if (entry.Ip is null)
        {
            throw new InvalidOperationException(
                $"ToNameConstraintSubtreeBytes requires an IP-backed HostEntry, got Kind={entry.Kind}");
        }
        var addr = entry.Ip.GetAddressBytes();
        var mask = BuildNetmask(addr.Length, entry.PrefixLength);
        var combined = new byte[addr.Length + mask.Length];
        Buffer.BlockCopy(addr, 0, combined, 0, addr.Length);
        Buffer.BlockCopy(mask, 0, combined, addr.Length, mask.Length);
        return combined;
    }

    /// <summary>URI authority host portion. IPv6 bracket-wrapped per RFC 3986 §3.2.2;
    /// throws on CIDR (no canonical URL form).</summary>
    public static string ToUrlHost(HostEntry entry) => entry.Kind switch
    {
        HostKind.Dns => entry.DnsName!,
        HostKind.Ipv4 => entry.Ip!.ToString(),
        HostKind.Ipv6 => $"[{entry.Ip}]",
        _ => throw new InvalidOperationException(
            $"HostEntry '{entry.Raw}' ({entry.Kind}) has no URL host representation"),
    };

    /// <summary>First non-CIDR entry — leaf CN, nginx <c>server_name</c>, and URL derivation
    /// primary. Null when the list is all-CIDR or empty.</summary>
    public static HostEntry? PickPrimary(IEnumerable<HostEntry> entries) =>
        entries.FirstOrDefault(e => e.IsLeafEligible);
}
