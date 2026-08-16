using Interfold.Bootstrapper.Cli;
using Interfold.Bootstrapper.Phases;

namespace Interfold.Bootstrapper.UnitTests;

/// <summary>
/// Drives <see cref="ConfigPhase.ApplyPreFillMdnsCheckAsync"/> directly via its
/// <c>hostnameFactory</c> + <c>probe</c> seams. The banner is Tier 1 of the two-tier mDNS
/// design: it runs BEFORE <see cref="ConfigPhase.PromptForConfig"/> on the interactive
/// fresh-config path so the operator sees the mDNS status up-front and can make an informed
/// call about whether to include <c>.local</c> names in their hosts list.
///
/// <para>
/// Tests focus on the (hostname-or-null) return value and probe-call side effects — the
/// exact banner wording is asserted by the DinD integration tests which shell out to the
/// real binary and read stdout. TUnit's Source-Generated engine wraps <see cref="Console.Out"/>
/// with an AsyncLocal-scoped capturer that our per-test <see cref="Console.SetOut"/> can't
/// reliably reach, so we deliberately don't rely on it here.
/// </para>
/// <para>
/// The install-yes / install-no interactive branches are covered by the same integration
/// tests (both require a real <see cref="AnsiConsole.Prompt"/> keystroke). Under the unit
/// harness we drive the "no TTY" fork via the <c>isInteractive</c> test seam on
/// <see cref="ConfigPhase.ApplyPreFillMdnsCheckAsync"/> — <see cref="Console.SetIn"/>
/// swaps the managed <see cref="Console.In"/> reader but does NOT change
/// <see cref="Console.IsInputRedirected"/>, which is bound to the OS-level fd state. A
/// `dotnet test` run from an interactive terminal would therefore land in the
/// AnsiConsole.Prompt branch and hang / error; the seam sidesteps that entirely.
/// </para>
/// <para>
/// <b>Serialisation via <c>[NotInParallel("bootstrapper-console")]</c></b> — every test
/// swaps process-global <see cref="Console.In"/> via <see cref="WithRedirectedStdinAsync"/>
/// and restores it in a finally. That swap races if any sibling test also touches the
/// process-global console streams; sharing the <c>bootstrapper-console</c> NotInParallel
/// key with <see cref="ConfigInteractivePromptTests"/> guarantees only one console-mutating
/// test in the assembly runs at a time. The same serialisation removes the
/// <c>NamedPipeServer</c> teardown race documented on
/// <see cref="ConfigInteractivePromptTests"/> — see that class for the full rationale.
/// </para>
/// </summary>
[NotInParallel("bootstrapper-console")]
public sealed class ConfigPreFillMdnsCheckTests
{
    private static BootstrapOptions OptionsFor(bool nonInteractive) => TestSupport.MakeOptions(
        command: BootstrapCommand.Bootstrap,
        outputDir: "./deploy",
        skipPrereqs: true,
        nonInteractive: nonInteractive);

    /// <summary>
    /// Ensures <see cref="Console.IsInputRedirected"/> is true so the banner takes the "no
    /// TTY" fork rather than blocking on a <see cref="AnsiConsole.Prompt"/> keystroke that
    /// the unit harness can't drive. Restores the original stdin in a finally so sibling
    /// tests aren't affected.
    /// </summary>
    private static async Task<TResult> WithRedirectedStdinAsync<TResult>(Func<Task<TResult>> body)
    {
        var prevIn = Console.In;
        try
        {
#pragma warning disable TUnit0055
            Console.SetIn(new StringReader(string.Empty));
#pragma warning restore TUnit0055
            return await body().ConfigureAwait(false);
        }
        finally
        {
#pragma warning disable TUnit0055
            Console.SetIn(prevIn);
#pragma warning restore TUnit0055
        }
    }

    [Test]
    public async Task PreFillReturnsNullWhenNonInteractive()
    {
        // Non-interactive callers get no banner — the post-fill gate handles any .local
        // entries the operator supplied in their config file. Documents the "the banner is
        // interactive-only" contract and pins that the probe is never called on this path
        // (probe throws to catch a regression that would silently start shelling out to
        // `getent` in non-interactive runs).
        var options = OptionsFor(nonInteractive: true);
        var logger = new PhaseLogger(options);
        var probeCalled = false;

        var result = await ConfigPhase.ApplyPreFillMdnsCheckAsync(
            options, logger, CancellationToken.None,
            hostnameFactory: () => "workstation.local",
            probe: (_, _) =>
            {
                probeCalled = true;
                return Task.FromResult<bool?>(true);
            });

        await Assert.That(result).IsNull();
        await Assert.That(probeCalled).IsFalse();
    }

    [Test]
    public async Task PreFillReturnsNullWhenNoHostname()
    {
        // hostnameFactory returns null when the device has no short-name suitable for mDNS
        // (empty, "localhost", already-qualified FQDN, invalid chars). In that case the
        // banner logs a single info line and skips the probe entirely — asserted here via
        // the "probe was never called" side effect since the info line goes through
        // PhaseLogger → Console.WriteLine which TUnit intercepts before our test can see it.
        var options = OptionsFor(nonInteractive: false);
        var logger = new PhaseLogger(options);
        var probeCalled = false;

        var result = await WithRedirectedStdinAsync(async () =>
            await ConfigPhase.ApplyPreFillMdnsCheckAsync(
                options, logger, CancellationToken.None,
                hostnameFactory: () => null,
                probe: (_, _) =>
                {
                    probeCalled = true;
                    return Task.FromResult<bool?>(true);
                }));

        await Assert.That(result).IsNull();
        await Assert.That(probeCalled).IsFalse();
    }

    [Test]
    public async Task PreFillReturnsNullOnNonLinuxProbe()
    {
        // The probe returns null on non-Linux (getent doesn't exist in the shape the check
        // needs) or on an environmental failure (getent binary missing, Process.Start
        // failure). Callers treat that as "unknown — skip the pre-fill" so operators on a
        // Windows / macOS dev workstation don't get an .local name silently baked into the
        // hosts row.
        var options = OptionsFor(nonInteractive: false);
        var logger = new PhaseLogger(options);
        var probedNames = new List<string>();

        var result = await WithRedirectedStdinAsync(async () =>
            await ConfigPhase.ApplyPreFillMdnsCheckAsync(
                options, logger, CancellationToken.None,
                hostnameFactory: () => "workstation.local",
                probe: (name, _) =>
                {
                    probedNames.Add(name);
                    return Task.FromResult<bool?>(null);
                }));

        await Assert.That(result).IsNull();
        // Exactly one probe — the null result short-circuits without attempting to install /
        // re-probe. If a regression re-probes after the initial null, this fails loudly.
        await Assert.That(probedNames.Count).IsEqualTo(1);
        // Pin the probe input so a future refactor that (say) starts qualifying the
        // hostname twice is caught immediately.
        await Assert.That(probedNames[0]).IsEqualTo("workstation.local");
    }

    [Test]
    public async Task PreFillReturnsHostnameWhenProbeResolves()
    {
        // Happy path — mDNS is working, {hostname}.local resolves via the local resolver
        // chain, and the banner logs a confirmation info line before returning the hostname
        // for the pre-fill. The returned hostname must match verbatim what the caller
        // (PromptForConfig) will use as the pre-fill's leading hosts entry.
        var options = OptionsFor(nonInteractive: false);
        var logger = new PhaseLogger(options);
        var probeCount = 0;

        var result = await WithRedirectedStdinAsync(async () =>
            await ConfigPhase.ApplyPreFillMdnsCheckAsync(
                options, logger, CancellationToken.None,
                hostnameFactory: () => "workstation.local",
                probe: (_, _) =>
                {
                    probeCount++;
                    return Task.FromResult<bool?>(true);
                }));

        await Assert.That(result).IsEqualTo("workstation.local");
        // Only one probe on the happy path — the "resolves" answer short-circuits the
        // install + re-probe branches entirely.
        await Assert.That(probeCount).IsEqualTo(1);
    }

    [Test]
    public async Task PreFillReturnsNullWhenProbeFailsAndNoTty()
    {
        // The probe returns false → mDNS is broken. In a real TTY we would then offer to
        // install avahi; the `isInteractive: () => false` seam forces the "no TTY" fork
        // so the install prompt is skipped. The banner still emits the "mDNS unavailable"
        // warning + install hint (unit tests can't capture the warn line — see class
        // summary), but the observable side effect is (a) result is null, (b) exactly
        // one probe call — the install-recheck probe never fires because the
        // confirmation prompt was skipped.
        //
        // NB: Console.SetIn in WithRedirectedStdinAsync doesn't affect
        // Console.IsInputRedirected — see the class summary. The seam is the load-bearing
        // mechanism here; the stdin redirect is belt-and-braces so a regression that
        // dropped the seam wouldn't hang the test on an AnsiConsole.Prompt keystroke.
        var options = OptionsFor(nonInteractive: false);
        var logger = new PhaseLogger(options);
        var probeCount = 0;

        var result = await WithRedirectedStdinAsync(async () =>
            await ConfigPhase.ApplyPreFillMdnsCheckAsync(
                options, logger, CancellationToken.None,
                hostnameFactory: () => "workstation.local",
                probe: (_, _) =>
                {
                    probeCount++;
                    return Task.FromResult<bool?>(false);
                },
                isInteractive: () => false));

        await Assert.That(result).IsNull();
        await Assert.That(probeCount).IsEqualTo(1);
    }

    [Test]
    public async Task PreFillPassesQualifiedHostnameToProbe()
    {
        // Bakes the "the probe is called with the mDNS-qualified name, not the raw short
        // hostname" contract into the test suite. HostnameDetector.QualifyForMdns is
        // responsible for the .local suffixing; ApplyPreFillMdnsCheckAsync must pass the
        // qualified name through verbatim so the getent lookup can traverse the
        // mdns_minimal NSS module.
        var options = OptionsFor(nonInteractive: false);
        var logger = new PhaseLogger(options);
        string? probedName = null;

        var result = await WithRedirectedStdinAsync(async () =>
            await ConfigPhase.ApplyPreFillMdnsCheckAsync(
                options, logger, CancellationToken.None,
                hostnameFactory: () => "my-server.local",
                probe: (name, _) =>
                {
                    probedName = name;
                    return Task.FromResult<bool?>(true);
                }));

        await Assert.That(probedName).IsEqualTo("my-server.local");
        await Assert.That(result).IsEqualTo("my-server.local");
    }
}
