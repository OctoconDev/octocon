using System.Runtime.InteropServices;
using Interfold.Bootstrapper.Cli;
using Interfold.Bootstrapper.Configuration;
using Interfold.Bootstrapper.Phases;
using Interfold.Bootstrapper.Util;
using Interfold.Shared.Contracts.Enums;
using TUnit.Core.Exceptions;

namespace Interfold.Bootstrapper.UnitTests;

/// <summary>
/// Validates the embed-with-override contract: open streams from assembly resources and
/// materialize to disk only when a host path is required.
/// </summary>
public sealed class EmbeddedSupportFilesTests
{
    private const string ResourcePrefix = EmbeddedSupportFiles.ResourcePrefix;

    private static BootstrapOptions OptionsFor() => TestSupport.MakeOptions(
        command: BootstrapCommand.Bootstrap,
        outputDir: Path.GetTempPath(),
        skipPrereqs: true,
        nonInteractive: true);

    private static IReadOnlyList<string> EnumerateSupportResources() =>
        EmbeddedSupportFiles.EnumerateSupportResourceNames();

    private static byte[] ReadResourceBytes(string resourceName)
    {
        using var stream = typeof(EmbeddedSupportFiles).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Manifest resource '{resourceName}' not found.");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    [Test]
    public async Task OpensEveryEmbeddedSupportResource()
    {
        var resources = EnumerateSupportResources();
        await Assert.That(resources.Count).IsGreaterThanOrEqualTo(10)
            .Because("at least the 10 originally-embedded support files should still ship");

        foreach (var name in resources)
        {
            var relative = name[ResourcePrefix.Length..];
            using var stream = EmbeddedSupportFiles.Open(relative);
            await Assert.That(stream.CanRead).IsTrue()
                .Because($"resource '{name}' should open for read");
        }
    }

    [Test]
    public async Task MaterializeWritesMissingFileWithOriginalContent()
    {
        using var scratch = TestSupport.NewScratchDir("interfold-embed");
        const string relative = "db/scylla/cassandra-rackdc.nam.properties";
        var target = Path.Combine(scratch.Path, "rackdc.properties");

        var wrote = EmbeddedSupportFiles.Materialize(relative, target, new PhaseLogger(OptionsFor()));
        await Assert.That(wrote).IsTrue();

        var expected = ReadResourceBytes(ResourcePrefix + relative);
        var actual = await File.ReadAllBytesAsync(target);
        await Assert.That(actual.SequenceEqual(expected)).IsTrue();
    }

    [Test]
    public async Task MaterializePreservesOperatorOverrideForExistingFiles()
    {
        using var scratch = TestSupport.NewScratchDir("interfold-embed");
        const string relative = "db/scylla/cassandra-rackdc.nam.properties";
        var target = Path.Combine(scratch.Path, "rackdc.properties");
        Directory.CreateDirectory(scratch.Path);

        var operatorContent = "# operator-customised content for unit test\n"u8.ToArray();
        await File.WriteAllBytesAsync(target, operatorContent);

        var wrote = EmbeddedSupportFiles.Materialize(relative, target, new PhaseLogger(OptionsFor()));
        await Assert.That(wrote).IsFalse();

        var afterRun = await File.ReadAllBytesAsync(target);
        await Assert.That(afterRun.SequenceEqual(operatorContent)).IsTrue();
    }

    [Test]
    public async Task MaterializeUnderSupportRootResolvesUnderOutputDir()
    {
        using var scratch = TestSupport.NewScratchDir("interfold-embed");
        var path = EmbeddedSupportFiles.MaterializeUnderSupportRoot(
            scratch.Path, EmbeddedSupportFiles.NginxTemplateRelative);

        await Assert.That(path.StartsWith(EmbeddedSupportFiles.SupportRoot(scratch.Path))).IsTrue();
        await Assert.That(File.Exists(path)).IsTrue();
    }

    [Test]
    public async Task SetsExecutableBitOnShellScriptsWhenMaterializedOnUnix()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
            && !RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            throw new SkipTestException("Executable-bit assertion is Unix-only.");
        }

        using var scratch = TestSupport.NewScratchDir("interfold-embed");
        const string relative = "scripts/docker/ensure-host-aio.sh";
        var target = Path.Combine(scratch.Path, "ensure-host-aio.sh");
        EmbeddedSupportFiles.Materialize(relative, target, new PhaseLogger(OptionsFor()));

        var mode = File.GetUnixFileMode(target);
        await Assert.That(mode.HasFlag(UnixFileMode.UserExecute)).IsTrue();
    }

    [Test]
    public async Task StagePublishSupportFilesMaterializesRackdcUnderOutputSupport()
    {
        using var scratch = TestSupport.NewScratchDir("interfold-publish-support");
        var config = new BootstrapConfig { DatabaseMode = DatabaseMode.Single };
        config.Deployment.Hosts = ["api.example.com"];
        ConfigPhase.ResolveDerivedDefaults(config);
        PublishPhase.StagePublishSupportFiles(config, scratch.Path, new PhaseLogger(OptionsFor()));

        var expected = EmbeddedSupportFiles.SupportFilePath(
            scratch.Path, EmbeddedSupportFiles.RackDcRelative("nam"));
        await Assert.That(File.Exists(expected)).IsTrue();
    }
}
