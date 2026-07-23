using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Interfold.Bootstrapper.Configuration;

/// <summary>Best-effort primary unicast IP used to pre-fill the interactive "Public host(s)"
/// row. Operator override wins; non-interactive flows skip this so a JSON file that omits
/// <c>hosts</c> still fails fast instead of minting a cert for whatever address happened to
/// be on the box. Enumeration faults degrade to null (no default) rather than crashing.</summary>
internal static class LocalAddressDetector
{
    // Prefix guard for NICs that report a normal type but are actually virtual: Docker/KVM
    // bridges, k8s overlays, Hyper-V switches, VPN tunnels. Without this the picker latches
    // onto Docker bridge 172.17.0.1 on dev boxes, which is never what the operator wants.
    private static readonly string[] VirtualNamePrefixes =
    [
        "docker",       // docker0, docker_gwbridge
        "br-",          // docker user-defined bridge networks
        "veth",         // container veth pairs
        "tap", "tun",   // OpenVPN, WireGuard userspace, qemu
        "vEthernet (",  // Windows Hyper-V virtual switches
        "vmnet",        // macOS VMware Fusion / Parallels
        "virbr",        // libvirt-managed bridges
        "cni",          // container networking interface plugins
        "flannel", "cilium_", "kube-", // k8s overlays
        "zt",           // ZeroTier
        "wg",           // WireGuard
        "utun",         // macOS userspace TUN (Tailscale, Cloudflare WARP)
        "tailscale",
    ];

    /// <summary>IPv4-biased; enumeration faults return null so the operator types a host.</summary>
    public static IPAddress? TryDetectPrimaryIp()
    {
        try
        {
            var nics = NetworkInterface.GetAllNetworkInterfaces();
            var probes = new List<NicProbe>(nics.Length);
            foreach (var nic in nics)
            {
                // Per-NIC try/catch: NICs can disappear mid-enumeration (USB unplug, VPN teardown).
                IReadOnlyList<IPAddress> addresses;
                try
                {
                    addresses = nic.GetIPProperties().UnicastAddresses
                        .Select(u => u.Address)
                        .ToArray();
                }
                catch (NetworkInformationException)
                {
                    addresses = [];
                }
                probes.Add(new NicProbe(
                    nic.Name,
                    nic.NetworkInterfaceType,
                    nic.OperationalStatus,
                    addresses));
            }
            return Pick(probes);
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }

    /// <summary>Pure picker over fabricated NIC probes. Rejects: NICs that aren't Up, Loopback
    /// or Tunnel types, any name matching <see cref="VirtualNamePrefixes"/>, loopback addresses,
    /// APIPA 169.254.0.0/16, and IPv6 link-local/site-local/multicast. First IPv4 wins; IPv6
    /// only if no usable IPv4 exists.</summary>
    public static IPAddress? Pick(IReadOnlyList<NicProbe> probes)
    {
        IPAddress? firstV4 = null;
        IPAddress? firstV6 = null;

        foreach (var nic in probes)
        {
            if (nic.Status != OperationalStatus.Up) continue;
            if (nic.Type is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            if (IsVirtualName(nic.Name)) continue;

            foreach (var addr in nic.Addresses)
            {
                if (IPAddress.IsLoopback(addr)) continue;

                switch (addr.AddressFamily)
                {
                    case AddressFamily.InterNetwork:
                        var v4Bytes = addr.GetAddressBytes();
                        // APIPA 169.254.0.0/16 == DHCP failure.
                        if (v4Bytes[0] == 169 && v4Bytes[1] == 254) continue;
                        firstV4 ??= addr;
                        break;

                    case AddressFamily.InterNetworkV6:
                        if (addr.IsIPv6LinkLocal) continue;
                        if (addr.IsIPv6SiteLocal) continue;
                        if (addr.IsIPv6Multicast) continue;
                        firstV6 ??= addr;
                        break;
                }
            }
        }

        return firstV4 ?? firstV6;
    }

    /// <summary>Case-insensitive prefix match against <see cref="VirtualNamePrefixes"/>.</summary>
    internal static bool IsVirtualName(string name)
    {
        foreach (var prefix in VirtualNamePrefixes)
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Test-friendly snapshot of one <see cref="NetworkInterface"/> — sealed and
    /// tightly coupled to the live OS query, so tests fabricate <c>NicProbe</c> instead.</summary>
    public readonly record struct NicProbe(
        string Name,
        NetworkInterfaceType Type,
        OperationalStatus Status,
        IReadOnlyList<IPAddress> Addresses);
}
