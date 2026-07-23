using System.Net;
using System.Net.NetworkInformation;
using Interfold.Bootstrapper.Configuration;

namespace Interfold.Bootstrapper.UnitTests;

/// <summary>
/// Unit tests for <see cref="LocalAddressDetector.Pick"/>. The picker is the only deterministic
/// surface — the live <see cref="LocalAddressDetector.TryDetectPrimaryIp"/> wrapper depends on
/// the runner's actual NICs and is therefore covered by a single smoke test below. Every other
/// path drives <see cref="LocalAddressDetector.Pick"/> with fabricated
/// <see cref="LocalAddressDetector.NicProbe"/> records so we exercise the filter chain
/// (status / type / virtual-name / loopback / APIPA / link-local / family preference) without
/// reaching into the OS.
/// </summary>
public sealed class LocalAddressDetectorTests
{
    private static LocalAddressDetector.NicProbe MakeProbe(
        string name,
        NetworkInterfaceType type = NetworkInterfaceType.Ethernet,
        OperationalStatus status = OperationalStatus.Up,
        string[]? addresses = null) =>
        new(name, type, status, (addresses ?? []).Select(IPAddress.Parse).ToArray());

    [Test]
    public async Task EmptyProbeListReturnsNull()
    {
        var result = LocalAddressDetector.Pick(Array.Empty<LocalAddressDetector.NicProbe>());
        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task LoopbackOnlyReturnsNull()
    {
        // Box with nothing but lo / lo0 — common in air-gapped CI containers. The picker has
        // to surface "no usable address" rather than silently returning 127.0.0.1, because a
        // loopback in a SAN produces a cert that only validates from the box itself.
        var probes = new[]
        {
            MakeProbe("lo", NetworkInterfaceType.Loopback, addresses: ["127.0.0.1", "::1"]),
        };
        var result = LocalAddressDetector.Pick(probes);
        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task DownInterfaceIsSkippedEvenWithUsableAddress()
    {
        // OperationalStatus.Down means the OS won't route through this NIC; its address is
        // therefore not a useful default for a serving cert.
        var probes = new[]
        {
            MakeProbe("eth0", status: OperationalStatus.Down, addresses: ["192.168.1.42"]),
        };
        var result = LocalAddressDetector.Pick(probes);
        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task SingleGoodEthernetReturnsItsIpv4()
    {
        var probes = new[]
        {
            MakeProbe("eth0", addresses: ["192.168.1.42"]),
        };
        var result = LocalAddressDetector.Pick(probes);
        await Assert.That(result!.ToString()).IsEqualTo("192.168.1.42");
    }

    [Test]
    public async Task LoopbackPlusEthernetPicksEthernet()
    {
        // The loopback NIC type is filtered out wholesale; the picker should latch onto the
        // routable Ethernet's address regardless of probe order.
        var probes = new[]
        {
            MakeProbe("lo", NetworkInterfaceType.Loopback, addresses: ["127.0.0.1"]),
            MakeProbe("eth0", addresses: ["10.0.0.5"]),
        };
        var result = LocalAddressDetector.Pick(probes);
        await Assert.That(result!.ToString()).IsEqualTo("10.0.0.5");
    }

    [Test]
    public async Task TunnelInterfaceIsSkipped()
    {
        // Tunnel NIC type (VPN endpoints, GRE tunnels, IPv6 transition tunnels) is operator-
        // specific and usually transient. We deliberately don't bake a VPN-assigned IP into a
        // cert SAN.
        var probes = new[]
        {
            MakeProbe("vpn0", type: NetworkInterfaceType.Tunnel, addresses: ["10.8.0.2"]),
            MakeProbe("eth0", addresses: ["192.168.1.42"]),
        };
        var result = LocalAddressDetector.Pick(probes);
        await Assert.That(result!.ToString()).IsEqualTo("192.168.1.42");
    }

    [Test]
    public async Task DockerBridgeIsSkipped()
    {
        // Docker / k8s / VPN-as-Ethernet bridges report NetworkInterfaceType.Ethernet on Linux
        // but their name prefixes (docker, br-, veth, cni, flannel, cilium_) tag them as
        // virtual. A dev laptop with both docker0 (172.17.0.1) and a real LAN NIC must pick the
        // LAN — picking docker0 would mint a cert that only the host validates, since 172.17
        // is rarely reachable from outside the box.
        var probes = new[]
        {
            MakeProbe("docker0", addresses: ["172.17.0.1"]),
            MakeProbe("br-abcdef123456", addresses: ["172.18.0.1"]),
            MakeProbe("eth0", addresses: ["192.168.1.42"]),
        };
        var result = LocalAddressDetector.Pick(probes);
        await Assert.That(result!.ToString()).IsEqualTo("192.168.1.42");
    }

    [Test]
    public async Task HyperVVirtualSwitchIsSkippedOnWindowsLikeName()
    {
        // Windows surfaces Hyper-V virtual switches with names like "vEthernet (Default Switch)".
        // The name-prefix filter must catch them.
        var probes = new[]
        {
            MakeProbe("vEthernet (Default Switch)", addresses: ["172.20.16.1"]),
            MakeProbe("Ethernet", addresses: ["192.168.1.42"]),
        };
        var result = LocalAddressDetector.Pick(probes);
        await Assert.That(result!.ToString()).IsEqualTo("192.168.1.42");
    }

    [Test]
    public async Task WireGuardAndTailscaleTunnelsAreSkipped()
    {
        // wg* / utun* / tailscale0 are common userspace tunnel device names that on some
        // platforms report as plain Ethernet (Tailscale on macOS via utunN, WireGuard on
        // Linux via wgN). Catch them by name even when the type-based filter wouldn't.
        var probes = new[]
        {
            MakeProbe("wg0", addresses: ["10.66.66.1"]),
            MakeProbe("utun4", addresses: ["100.64.0.1"]),
            MakeProbe("tailscale0", addresses: ["100.64.0.2"]),
            MakeProbe("en0", addresses: ["192.168.1.42"]),
        };
        var result = LocalAddressDetector.Pick(probes);
        await Assert.That(result!.ToString()).IsEqualTo("192.168.1.42");
    }

    [Test]
    public async Task Ipv4ApipaIsSkipped()
    {
        // 169.254.0.0/16 is the IPv4 link-local fallback set when DHCP fails. It indicates
        // "no real network" — never a useful SAN value.
        var probes = new[]
        {
            MakeProbe("eth0", addresses: ["169.254.42.42"]),
        };
        var result = LocalAddressDetector.Pick(probes);
        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task ApipaPlusRealIpv4PicksRealIp()
    {
        // Within a single NIC's address list APIPA should be filtered while the routable IPv4
        // wins.
        var probes = new[]
        {
            MakeProbe("eth0", addresses: ["169.254.42.42", "10.0.0.5"]),
        };
        var result = LocalAddressDetector.Pick(probes);
        await Assert.That(result!.ToString()).IsEqualTo("10.0.0.5");
    }

    [Test]
    public async Task Ipv6LinkLocalIsSkipped()
    {
        // fe80::/10 link-local is fundamentally scoped to the local link and never useful in a
        // SAN. Same logic as IPv4 APIPA but for the v6 family.
        var probes = new[]
        {
            MakeProbe("eth0", addresses: ["fe80::1234"]),
        };
        var result = LocalAddressDetector.Pick(probes);
        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task DualStackPrefersIpv4()
    {
        // The picker's documented IPv4 bias: a NIC exposing both 192.168.1.42 and 2001:db8::1
        // returns the v4 address, matching the dominant self-host topology (LAN behind v4 NAT).
        // Operators on a pure-v6 deployment still get a usable default via the next test.
        var probes = new[]
        {
            MakeProbe("eth0", addresses: ["2001:db8::1", "192.168.1.42"]),
        };
        var result = LocalAddressDetector.Pick(probes);
        await Assert.That(result!.ToString()).IsEqualTo("192.168.1.42");
    }

    [Test]
    public async Task Ipv6OnlyDeploymentReturnsIpv6()
    {
        // No usable v4 candidate (loopback + APIPA filtered) → the picker falls back to the
        // first usable v6. Common for hosted IPv6-only VPSes.
        var probes = new[]
        {
            MakeProbe("eth0", addresses: ["169.254.42.42", "2001:db8::1"]),
        };
        var result = LocalAddressDetector.Pick(probes);
        await Assert.That(result!.ToString()).IsEqualTo("2001:db8::1");
    }

    [Test]
    public async Task NonLoopbackNicWithLoopbackAddressIsStillSkipped()
    {
        // Defensive double-check: a NIC reporting type Ethernet but exposing 127.0.0.1 (rare
        // mis-config, but observed on some embedded boxes) shouldn't leak the loopback into
        // the picker's output. The IPAddress.IsLoopback check inside the address loop catches it.
        var probes = new[]
        {
            MakeProbe("eth0", addresses: ["127.0.0.1"]),
        };
        var result = LocalAddressDetector.Pick(probes);
        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task LiveDetectorRunsWithoutThrowing()
    {
        // Smoke test for the live path: TryDetectPrimaryIp must never throw — any platform
        // surprise (no NICs, permissions, NetworkInformationException from a NIC disappearing
        // mid-enumeration) is swallowed and reported as null. We don't assert WHAT it returns
        // (depends on the runner's NICs), only that the call completes and produces either
        // null or a non-loopback address.
        var result = LocalAddressDetector.TryDetectPrimaryIp();
        if (result is not null)
        {
            await Assert.That(IPAddress.IsLoopback(result)).IsFalse();
        }
    }

    [Test]
    public async Task IsVirtualNameMatchesEveryDocumentedPrefix()
    {
        // Pins the documented prefix list against representative real-world NIC names. If a
        // future refactor accidentally drops a prefix (e.g. removes "cni"), this test fails
        // before the picker silently starts returning a k8s overlay IP as the "primary" host.
        await Assert.That(LocalAddressDetector.IsVirtualName("docker0")).IsTrue();
        await Assert.That(LocalAddressDetector.IsVirtualName("br-abc123")).IsTrue();
        await Assert.That(LocalAddressDetector.IsVirtualName("veth1234abcd")).IsTrue();
        await Assert.That(LocalAddressDetector.IsVirtualName("tap0")).IsTrue();
        await Assert.That(LocalAddressDetector.IsVirtualName("tun0")).IsTrue();
        await Assert.That(LocalAddressDetector.IsVirtualName("vEthernet (Default Switch)")).IsTrue();
        await Assert.That(LocalAddressDetector.IsVirtualName("vmnet8")).IsTrue();
        await Assert.That(LocalAddressDetector.IsVirtualName("virbr0")).IsTrue();
        await Assert.That(LocalAddressDetector.IsVirtualName("cni0")).IsTrue();
        await Assert.That(LocalAddressDetector.IsVirtualName("flannel.1")).IsTrue();
        await Assert.That(LocalAddressDetector.IsVirtualName("cilium_host")).IsTrue();
        await Assert.That(LocalAddressDetector.IsVirtualName("kube-bridge")).IsTrue();
        await Assert.That(LocalAddressDetector.IsVirtualName("zt-abc123")).IsTrue();
        await Assert.That(LocalAddressDetector.IsVirtualName("wg0")).IsTrue();
        await Assert.That(LocalAddressDetector.IsVirtualName("utun4")).IsTrue();
        await Assert.That(LocalAddressDetector.IsVirtualName("tailscale0")).IsTrue();

        // Common real-NIC names that should NOT match — these are the operator's actual LAN
        // interfaces and must reach the picker.
        await Assert.That(LocalAddressDetector.IsVirtualName("eth0")).IsFalse();
        await Assert.That(LocalAddressDetector.IsVirtualName("en0")).IsFalse();
        await Assert.That(LocalAddressDetector.IsVirtualName("wlan0")).IsFalse();
        await Assert.That(LocalAddressDetector.IsVirtualName("Ethernet")).IsFalse();
        await Assert.That(LocalAddressDetector.IsVirtualName("Wi-Fi")).IsFalse();
    }

    [Test]
    public async Task PrefixMatchIsCaseInsensitive()
    {
        // Windows often capitalises NIC names ("Ethernet", "VEthernet (..)"). The match has to
        // be case-insensitive so platform-specific casing doesn't slip a virtual NIC past the
        // filter.
        await Assert.That(LocalAddressDetector.IsVirtualName("Docker0")).IsTrue();
        await Assert.That(LocalAddressDetector.IsVirtualName("VETHERNET (test)")).IsTrue();
        await Assert.That(LocalAddressDetector.IsVirtualName("Tailscale0")).IsTrue();
    }
}
