using System.Runtime.InteropServices;
using Interfold.Bootstrapper.Cli;
using Interfold.Bootstrapper.Util;
using TUnit.Core;
using TUnit.Core.Exceptions;

namespace Interfold.Bootstrapper.UnitTests;

/// <summary>
/// Validates the embed-with-override contract that <see cref="EmbeddedSupportFiles"/> upholds:
///   * Every <c>&lt;EmbeddedResource&gt;</c> in the bootstrapper csproj with a <c>support/</c>
///     LogicalName ends up on disk under <c>scratch.Path</c> with the byte-for-byte original content.
///   * A pre-existing file (power-user override) is never overwritten.
///   * Shell scripts gain the executable bit on Unix-like hosts so an operator can invoke them
///     directly without remembering to chmod first.
///
/// These cases used to be implicit in the integration-test fixture (BootstrapperBuild manually
/// staged the same files); after the migration to embedded resources the contract lives in
/// process and deserves a sub-second unit-level check.
/// </summary>
public sealed class EmbeddedSupportFilesTests
{
    private const string ResourcePrefix = "support/";

    private static BootstrapOptions OptionsFor() => TestSupport.MakeOptions(
        command: BootstrapCommand.Bootstrap,
        outputDir: Path.GetTempPath(),
        skipPrereqs: true,
        nonInteractive: true);


    private static IReadOnlyList<string> EnumerateSupportResources() =>
        typeof(EmbeddedSupportFiles).Assembly
            .GetManifestResourceNames()
            .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            .ToList();

    private static byte[] ReadResourceBytes(string resourceName)
    {
        using var stream = typeof(EmbeddedSupportFiles).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Manifest resource '{resourceName}' not found.");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    [Test]
    public async Task ExtractsEveryEmbeddedResourceWithOriginalContent()
    {
        using var scratch = TestSupport.NewScratchDir("interfold-embed");
        var resources = EnumerateSupportResources();
        // Guards against regression: if a future refactor drops every embedded resource the
        // test would otherwise pass vacuously. The inventory at the time of writing is
        // 7 rackdc properties + ensure-host-aio.sh + default.conf.template + Cassandra
        // Dockerfile; allow growth but require some entries to be present.
        await Assert.That(resources.Count).IsGreaterThanOrEqualTo(10)
            .Because("at least the 10 originally-embedded support files should still ship");

        EmbeddedSupportFiles.EnsureExtracted(scratch.Path, new PhaseLogger(OptionsFor()));

        foreach (var name in resources)
        {
            var relPath = name[ResourcePrefix.Length..]
                .Replace('/', Path.DirectorySeparatorChar);
            var onDisk = Path.Combine(scratch.Path, relPath);

            await Assert.That(File.Exists(onDisk)).IsTrue()
                .Because($"resource '{name}' should have been extracted to '{onDisk}'");

            var actual = await File.ReadAllBytesAsync(onDisk);
            var expected = ReadResourceBytes(name);
            await Assert.That(actual.SequenceEqual(expected)).IsTrue()
                .Because($"content mismatch for '{name}'");
        }
    }

    [Test]
    public async Task PreservesOperatorOverrideForExistingFiles()
    {
        using var scratch = TestSupport.NewScratchDir("interfold-embed");
        // Pick the first rackdc resource as the "operator has hand-edited this" case.
        const string targetResource = "support/db/scylla/cassandra-rackdc.nam.properties";
        var resources = EnumerateSupportResources();
        await Assert.That(resources.Contains(targetResource)).IsTrue()
            .Because($"test depends on '{targetResource}' existing as an embedded resource");

        var relPath = targetResource[ResourcePrefix.Length..]
            .Replace('/', Path.DirectorySeparatorChar);
        var onDisk = Path.Combine(scratch.Path, relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(onDisk)!);

        // Stage a custom payload that is deliberately NOT what the embedded resource holds.
        var operatorContent = "# operator-customised content for unit test\n"u8.ToArray();
        await File.WriteAllBytesAsync(onDisk, operatorContent);

        EmbeddedSupportFiles.EnsureExtracted(scratch.Path, new PhaseLogger(OptionsFor()));

        // The operator file should still hold its custom bytes verbatim...
        var afterRun = await File.ReadAllBytesAsync(onDisk);
        await Assert.That(afterRun.SequenceEqual(operatorContent)).IsTrue()
            .Because("operator-edited file must not be overwritten by extraction");

        // ...and every OTHER resource should still have been extracted alongside it.
        foreach (var name in resources.Where(n => n != targetResource))
        {
            var otherPath = Path.Combine(
                scratch.Path,
                name[ResourcePrefix.Length..].Replace('/', Path.DirectorySeparatorChar));
            await Assert.That(File.Exists(otherPath)).IsTrue()
                .Because($"sibling resource '{name}' must still extract when one path is pre-populated");
        }
    }

    [Test]
    public async Task SetsExecutableBitOnShellScriptsOnUnix()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
            && !RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            // UnixFileMode is a no-op on Windows; assert nothing rather than producing a misleading green.
            throw new SkipTestException("Executable-bit assertion is Unix-only.");
        }

        using var scratch = TestSupport.NewScratchDir("interfold-embed");
        EmbeddedSupportFiles.EnsureExtracted(scratch.Path, new PhaseLogger(OptionsFor()));

        var scriptPath = Path.Combine(scratch.Path, "scripts", "docker", "ensure-host-aio.sh");
        await Assert.That(File.Exists(scriptPath)).IsTrue()
            .Because("ensure-host-aio.sh should have been extracted before the chmod check");

        var mode = File.GetUnixFileMode(scriptPath);
        await Assert.That(mode.HasFlag(UnixFileMode.UserExecute)).IsTrue()
            .Because("operator should be able to invoke the recovery script directly: ./ensure-host-aio.sh");
    }

}
