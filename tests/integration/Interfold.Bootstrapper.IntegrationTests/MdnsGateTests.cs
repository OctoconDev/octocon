using System.Text;
using System.Text.Json.Nodes;
using Interfold.Bootstrapper.IntegrationTests.Attributes;
using Interfold.Bootstrapper.IntegrationTests.Fixtures;

namespace Interfold.Bootstrapper.IntegrationTests;

/// <summary>
/// End-to-end coverage for the post-fill mDNS gate
/// (<c>ConfigPhase.ApplyMdnsGateAsync</c>) running against a real Ubuntu DinD where
/// <c>avahi-daemon</c> / <c>libnss-mdns</c> is not installed. Complements the deterministic
/// unit tests in <c>ConfigMdnsGateTests</c>: the unit tests drive the probe seam directly,
/// this test exercises the whole gate — the shelled-out <c>getent hosts</c> lookup, the
/// PhaseLogger warning wording surfaced through stderr, and the strip + subsequent
/// <c>Validate</c> re-run — all in a container that reproduces the "operator on a fresh
/// Ubuntu box without mDNS" scenario.
///
/// <para>
/// The fixture ships <c>definitely-not-resolvable.local</c> alongside <c>api.test.local</c>
/// and <c>127.0.0.1</c>. In a stock Ubuntu container without avahi none of the
/// <c>.local</c> entries can resolve, so the gate must strip both, leaving
/// <c>127.0.0.1</c> as the sole surviving host — enough for <c>Validate</c> to pass and
/// bootstrap to continue. If a regression starts halting on this path (or worse, silently
/// keeps the broken names in the SANs of the generated leaf cert), this test catches it.
/// </para>
/// </summary>
[RequiresDocker]
[ClassDataSource<UbuntuDinDFixture>(Shared = SharedType.PerTestSession)]
[Explicit]
public class MdnsGateTests(UbuntuDinDFixture dinD)
{

    [After(Test)]
    public Task DumpOnFailure(TestContext ctx) => DinDHookHelpers.DumpOnFailureAsync(dinD, ctx);

    [Test]
    public async Task NonInteractiveBootstrapStripsUnresolvableLocalHostsAndContinues()
    {
        // Must invoke `bootstrap` (not `publish`) — ApplyMdnsGateAsync is deliberately gated to
        // BootstrapCommand.Bootstrap only (see ConfigPhase.cs's `if (options.Command == BootstrapCommand.Bootstrap)`
        // guard around the gate call). The `publish` / `up` / `update-images` / etc. paths
        // load the persisted JSON verbatim and never re-warn about stale .local entries — the
        // operator re-runs `bootstrap` to pick up the strip. To keep the test fast we pair
        // `bootstrap` with --skip-prereqs (avoids apt install) and --fault-inject=after-publish
        // (halts cleanly right after PublishPhase, so certs + compose YAML are produced but
        // db-init / launch / compose-up never run).
        var scratch = await dinD.CreateScratchAsync(
            nameof(NonInteractiveBootstrapStripsUnresolvableLocalHostsAndContinues),
            TestConfigPaths.MdnsGateConfig);

        var result = await dinD.RunOnScratchAsync(scratch,
            nameof(NonInteractiveBootstrapStripsUnresolvableLocalHostsAndContinues),
            "bootstrap",
            "--skip-prereqs", "--fault-inject=after-publish");

        // Exit 0 pins the "bootstrap always continues" contract. A regression that (say) started
        // treating the strip as a fatal error would flip this to non-zero and fail loudly.
        await Assert.That(result.ExitCode).IsEqualTo(0)
            .Because($"non-interactive bootstrap must continue after stripping unresolvable .local hosts: {result.Stderr}");

        // The warning wording is part of the operator UX contract — it must make clear
        // (a) which hosts were removed, (b) that the current run is continuing, and
        // (c) how to restore the entries on a future run. The stdout captures both stdout
        // and stderr on this code path because PhaseLogger.Warn goes through Console.WriteLine
        // (stdout), not Console.Error.
        var combined = result.Stdout + result.Stderr;
        await Assert.That(combined).Contains("removing unresolvable .local host(s)")
            .Because("gate must warn about the strip so the operator sees why the SAN list shrank");
        await Assert.That(combined).Contains("bootstrap will continue")
            .Because("gate warning must emphasise the current run is continuing — this is the load-bearing wording that pins the 'no halt' invariant");
        await Assert.That(combined).Contains("definitely-not-resolvable.local")
            .Because("gate must name the specific hosts it removed so the operator can act on them");

        // The persisted config on disk must reflect the strip — the caller re-persists after
        // mutation so a subsequent `bootstrap` re-run sees the pruned list and doesn't
        // re-issue the same warning for the same broken hosts.
        var persistedBytes = await dinD.CopyOutAsync(scratch.ConfigPath);
        var persistedJson = JsonNode.Parse(persistedBytes)
            ?? throw new InvalidOperationException("persisted bootstrap config parsed to null");
        var hosts = persistedJson["deployment"]?["hosts"]?.AsArray()
            ?? throw new InvalidOperationException("deployment.hosts missing from persisted config");
        var hostList = hosts.Select(h => h!.GetValue<string>()).ToList();

        await Assert.That(hostList).DoesNotContain("definitely-not-resolvable.local")
            .Because("gate must remove the unresolvable .local entry from the persisted config, not just from the in-memory copy");
        // api.test.local also gets stripped — no avahi in the DinD means every .local fails to
        // resolve, and the gate probes each entry individually.
        await Assert.That(hostList).DoesNotContain("api.test.local")
            .Because("every unresolvable .local entry must be stripped, not just the first failure");
        await Assert.That(hostList).Contains("127.0.0.1")
            .Because("non-.local entries must survive the strip so Validate has at least one host to work with");

        // The generated leaf cert must NOT include the stripped .local names as SANs. This is the
        // "the strip actually flows through to the produced artifacts, not just the log line"
        // check that guarantees a downstream consumer's TLS handshake against 127.0.0.1 doesn't
        // trip a "certificate has a SAN for a name that doesn't resolve" warning in strict clients.
        var leafBytes = await dinD.CopyOutAsync($"{scratch.OutputDir}/certs/leaf.crt");
        using var leaf = System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPem(
            Encoding.UTF8.GetString(leafBytes));
        var sanExt = leaf.Extensions
            .OfType<System.Security.Cryptography.X509Certificates.X509SubjectAlternativeNameExtension>()
            .FirstOrDefault();
        await Assert.That(sanExt).IsNotNull().Because("leaf cert must have a SAN extension");
        var sans = sanExt!.EnumerateDnsNames().ToList();
        await Assert.That(sans).DoesNotContain("definitely-not-resolvable.local")
            .Because("stripped .local entries must not leak into the leaf cert's SAN list");
        await Assert.That(sans).DoesNotContain("api.test.local")
            .Because("stripped .local entries must not leak into the leaf cert's SAN list");
    }
}



