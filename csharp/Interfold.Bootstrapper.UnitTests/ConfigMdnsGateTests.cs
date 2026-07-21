using Interfold.Bootstrapper.Cli;
using Interfold.Bootstrapper.Configuration;
using Interfold.Bootstrapper.Phases;
using TUnit.Core;

namespace Interfold.Bootstrapper.UnitTests;

/// <summary>
/// Drives <see cref="ConfigPhase.ApplyMdnsGateAsync"/> directly via its <c>probe</c> seam. The
/// gate is Tier 2 of the two-tier mDNS design: it runs AFTER
/// <see cref="ConfigPhase.Validate"/> on the <see cref="BootstrapCommand.Bootstrap"/> command
/// and catches any <c>.local</c> entries that survived the operator's typing (either they
/// declined the pre-prompt banner's install offer and typed one anyway, or the config was
/// loaded from JSON — which bypasses the banner entirely).
///
/// <para>
/// Tests focus on the return-value contract (true = mutated, false = no-op) and the
/// observable mutations to <see cref="DeploymentSection.Hosts"/>. The exact warning wording
/// (including the "bootstrap will continue" copy that pins the "we don't halt" invariant)
/// is asserted end-to-end by the DinD integration tests where a real subprocess writes to a
/// real stdout — TUnit's Source-Generated engine intercepts <see cref="Console.Out"/> ahead
/// of any per-test <see cref="Console.SetOut"/> swap, so unit tests can't reliably read
/// PhaseLogger output.
/// </para>
/// <para>
/// Interactive install-yes / install-no branches for the post-fill gate stay covered by
/// the integration tests (they need a real <see cref="Spectre.Console.AnsiConsole"/>
/// confirmation prompt keystroke). Here we lock down the skip / accept / strip
/// branches deterministically via the probe seam plus the Validate re-run downstream of
/// an emptied hosts list.
/// </para>
/// </summary>
public sealed class ConfigMdnsGateTests
{
    private static BootstrapOptions OptionsFor(bool nonInteractive) => TestSupport.MakeOptions(
        command: BootstrapCommand.Bootstrap,
        outputDir: "./deploy",
        skipPrereqs: true,
        nonInteractive: nonInteractive);

    /// <summary>
    /// Constructs a minimal <see cref="BootstrapConfig"/> with the supplied hosts. Every
    /// other field stays at its property-initialiser default — the gate doesn't touch
    /// anything except <c>Deployment.Hosts</c>, so this keeps the tests focused on the
    /// hosts-mutation behaviour without unrelated field wiring.
    /// </summary>
    private static BootstrapConfig ConfigWithHosts(params string[] hosts)
    {
        var config = new BootstrapConfig();
        config.Deployment.Hosts = [.. hosts];
        return config;
    }

    [Test]
    public async Task GateSkipsWhenNoLocalHostInList()
    {
        // Hosts list contains only non-.local entries — nothing for the gate to probe. Must
        // short-circuit BEFORE calling the probe (probe throws so we catch the regression
        // that would silently start shelling out to `getent` for non-.local hosts and
        // burn a getent per config on every bootstrap run).
        var options = OptionsFor(nonInteractive: true);
        var logger = new PhaseLogger(options);
        var config = ConfigWithHosts("api.example.com", "192.168.1.42");

        var mutated = await ConfigPhase.ApplyMdnsGateAsync(
            config, options, logger, CancellationToken.None,
            probe: (_, _) => throw new InvalidOperationException(
                "probe must not be called when no .local entries are present"));

        await Assert.That(mutated).IsFalse();
        await Assert.That(config.Deployment.Hosts.Count).IsEqualTo(2);
        await Assert.That(config.Deployment.Hosts).Contains("api.example.com");
        await Assert.That(config.Deployment.Hosts).Contains("192.168.1.42");
    }

    [Test]
    public async Task GateSkipsWhenProbeReturnsNull()
    {
        // Non-Linux short-circuit path — the probe returns null and the gate reports "no
        // mutation" so callers keep the operator's original list intact. Same behaviour
        // applies to any environmental failure the probe surfaces as null (getent binary
        // missing, Process.Start failure). Contract: the gate NEVER strips on ambiguity.
        var options = OptionsFor(nonInteractive: true);
        var logger = new PhaseLogger(options);
        var config = ConfigWithHosts("workstation.local", "192.168.1.42");

        var mutated = await ConfigPhase.ApplyMdnsGateAsync(
            config, options, logger, CancellationToken.None,
            probe: (_, _) => Task.FromResult<bool?>(null));

        await Assert.That(mutated).IsFalse();
        await Assert.That(config.Deployment.Hosts.Count).IsEqualTo(2);
        await Assert.That(config.Deployment.Hosts[0]).IsEqualTo("workstation.local");
    }

    [Test]
    public async Task GateAcceptsWhenAllLocalHostsResolve()
    {
        // Every .local host resolves via the local mDNS chain — nothing to strip. The gate
        // logs an "mDNS ok" confirmation info line and reports no mutation. Pins the
        // "silent-when-things-work" path so we don't accidentally start warning on healthy
        // configs.
        var options = OptionsFor(nonInteractive: true);
        var logger = new PhaseLogger(options);
        var config = ConfigWithHosts("workstation.local", "printer.local", "192.168.1.42");

        var mutated = await ConfigPhase.ApplyMdnsGateAsync(
            config, options, logger, CancellationToken.None,
            probe: (_, _) => Task.FromResult<bool?>(true));

        await Assert.That(mutated).IsFalse();
        await Assert.That(config.Deployment.Hosts.Count).IsEqualTo(3);
    }

    [Test]
    public async Task GateStripsUnresolvableLocalHostInNonInteractive()
    {
        // The bread-and-butter unattended path: mDNS is broken (avahi missing on the host)
        // and we're running from a systemd timer / scheduled job. The gate can't prompt, so
        // it strips the unresolvable .local entries and returns true so the caller knows to
        // re-persist + re-validate. The bootstrap run itself continues with the reduced
        // list — asserted by the caller re-running Validate() successfully in
        // GateStripsPreservingNonLocalHostsAllowsRevalidate below.
        var options = OptionsFor(nonInteractive: true);
        var logger = new PhaseLogger(options);
        var config = ConfigWithHosts("workstation.local", "192.168.1.42");

        var mutated = await ConfigPhase.ApplyMdnsGateAsync(
            config, options, logger, CancellationToken.None,
            probe: (_, _) => Task.FromResult<bool?>(false));

        await Assert.That(mutated).IsTrue();
        await Assert.That(config.Deployment.Hosts.Count).IsEqualTo(1);
        await Assert.That(config.Deployment.Hosts[0]).IsEqualTo("192.168.1.42");
        await Assert.That(config.Deployment.Hosts).DoesNotContain("workstation.local");
    }

    [Test]
    public async Task GateStripsAllBrokenLocalHostsIndividually()
    {
        // Every .local host in the list is broken — the gate must strip them all rather
        // than stopping at the first failure. Documents the "probe every entry"
        // per-hostname behaviour so a future refactor that (say) short-circuits on the
        // first failure doesn't silently leave stale entries in the list.
        var options = OptionsFor(nonInteractive: true);
        var logger = new PhaseLogger(options);
        var config = ConfigWithHosts("host-a.local", "host-b.local", "192.168.1.42");

        var mutated = await ConfigPhase.ApplyMdnsGateAsync(
            config, options, logger, CancellationToken.None,
            probe: (_, _) => Task.FromResult<bool?>(false));

        await Assert.That(mutated).IsTrue();
        await Assert.That(config.Deployment.Hosts.Count).IsEqualTo(1);
        await Assert.That(config.Deployment.Hosts).DoesNotContain("host-a.local");
        await Assert.That(config.Deployment.Hosts).DoesNotContain("host-b.local");
        await Assert.That(config.Deployment.Hosts).Contains("192.168.1.42");
    }

    [Test]
    public async Task GateStripsOnlyBrokenLocalHostsWhenSomeResolve()
    {
        // Mixed working / broken .local entries in the same list — only the broken ones
        // should be removed. Pins the "per-entry, not all-or-nothing" strip contract:
        // partial mDNS coverage is legitimate (e.g. some devices advertise, others don't)
        // and the gate must preserve the working entries so their SANs land in the leaf
        // cert as expected.
        var options = OptionsFor(nonInteractive: true);
        var logger = new PhaseLogger(options);
        var config = ConfigWithHosts("good.local", "bad.local", "192.168.1.42");

        var mutated = await ConfigPhase.ApplyMdnsGateAsync(
            config, options, logger, CancellationToken.None,
            probe: (name, _) => Task.FromResult<bool?>(name == "good.local"));

        await Assert.That(mutated).IsTrue();
        await Assert.That(config.Deployment.Hosts.Count).IsEqualTo(2);
        await Assert.That(config.Deployment.Hosts).Contains("good.local");
        await Assert.That(config.Deployment.Hosts).DoesNotContain("bad.local");
        await Assert.That(config.Deployment.Hosts).Contains("192.168.1.42");
    }

    [Test]
    public async Task GateStripsPreservingNonLocalHostsAllowsRevalidate()
    {
        // Downstream contract test: after the gate strips a broken .local entry, the
        // caller re-runs Validate() (see ConfigPhase.RunAsync). The remaining non-.local
        // host must be enough to satisfy validation — the strip is safe as long as at
        // least one leaf-eligible host survives.
        var options = OptionsFor(nonInteractive: true);
        var logger = new PhaseLogger(options);
        var config = ConfigWithHosts("workstation.local", "192.168.1.42");

        var mutated = await ConfigPhase.ApplyMdnsGateAsync(
            config, options, logger, CancellationToken.None,
            probe: (_, _) => Task.FromResult<bool?>(false));

        await Assert.That(mutated).IsTrue();
        // Validate() must succeed on the reduced list. If a regression changed the strip
        // to (say) leave the entry in as a placeholder, this would still pass; but if a
        // regression removed the non-.local guard from Validate, this catches the
        // downstream breakage.
        ConfigPhase.Validate(config);
    }

    [Test]
    public async Task GateEmptiesHostListWhenAllUnresolvable()
    {
        // The safety net: if a config with ONLY unresolvable .local entries goes through
        // the gate in non-interactive mode, the strip empties the Hosts list. Bootstrap
        // deliberately does NOT salvage — the caller (ConfigPhase.RunAsync) calls
        // Validate after the mutation, and Validate throws the standard "at least one
        // host required" error. Better to fail loudly than silently produce a cert with
        // zero SANs.
        var options = OptionsFor(nonInteractive: true);
        var logger = new PhaseLogger(options);
        var config = ConfigWithHosts("workstation.local");

        var mutated = await ConfigPhase.ApplyMdnsGateAsync(
            config, options, logger, CancellationToken.None,
            probe: (_, _) => Task.FromResult<bool?>(false));

        await Assert.That(mutated).IsTrue();
        await Assert.That(config.Deployment.Hosts.Count).IsEqualTo(0);
        // The caller's subsequent Validate() throws with the "at least one host" invariant.
        // Confirm the exact contract here so a future refactor that (say) silently retains
        // the broken entry to keep validation happy gets caught.
        var ex = Assert.Throws<InvalidOperationException>(() => ConfigPhase.Validate(config));
        await Assert.That(ex!.Message).Contains("must contain at least one host");
    }

    [Test]
    public async Task GateCaseInsensitivelyMatchesLocalSuffix()
    {
        // Operators sometimes uppercase parts of hostnames when hand-editing JSON. The
        // suffix match must be case-insensitive so "WORKSTATION.Local" is still recognised
        // as a .local entry and probed / stripped identically to "workstation.local".
        var options = OptionsFor(nonInteractive: true);
        var logger = new PhaseLogger(options);
        var config = ConfigWithHosts("WORKSTATION.Local", "192.168.1.42");

        var mutated = await ConfigPhase.ApplyMdnsGateAsync(
            config, options, logger, CancellationToken.None,
            probe: (_, _) => Task.FromResult<bool?>(false));

        await Assert.That(mutated).IsTrue();
        await Assert.That(config.Deployment.Hosts.Count).IsEqualTo(1);
        await Assert.That(config.Deployment.Hosts[0]).IsEqualTo("192.168.1.42");
    }

    [Test]
    public async Task GateProbesEveryLocalEntry()
    {
        // Contract: every .local entry gets its own probe call — no dedupe, no "first
        // failure short-circuits the rest". Documents that the probe fires per-entry so an
        // aggressive optimisation can't silently break the "per-hostname strip" behaviour
        // asserted by GateStripsOnlyBrokenLocalHostsWhenSomeResolve.
        var options = OptionsFor(nonInteractive: true);
        var logger = new PhaseLogger(options);
        var config = ConfigWithHosts("one.local", "two.local", "three.local");
        var probed = new List<string>();

        var mutated = await ConfigPhase.ApplyMdnsGateAsync(
            config, options, logger, CancellationToken.None,
            probe: (name, _) =>
            {
                probed.Add(name);
                return Task.FromResult<bool?>(true);
            });

        await Assert.That(mutated).IsFalse();
        await Assert.That(probed.Count).IsEqualTo(3);
        await Assert.That(probed).Contains("one.local");
        await Assert.That(probed).Contains("two.local");
        await Assert.That(probed).Contains("three.local");
    }
}
