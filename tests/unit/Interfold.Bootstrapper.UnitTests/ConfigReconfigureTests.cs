using System.Text.Json;
using Interfold.Bootstrapper.Cli;
using Interfold.Bootstrapper.Configuration;
using Interfold.Bootstrapper.Phases;

namespace Interfold.Bootstrapper.UnitTests;

/// <summary>ConfigPhase <c>--reconfigure</c> gate: refuses NonInteractive / redirected stdin
/// when an existing JSON is present, so CI never blocks on Spectre prompts.</summary>
public sealed class ConfigReconfigureTests
{
    [Test]
    public async Task ReconfigureWithNonInteractive_ThrowsBeforePrompt()
    {
        using var scratch = TestSupport.NewScratchDir("interfold-reconfigure-ni");
        var tmpDir = scratch.Path;
        var configPath = Path.Combine(tmpDir, "interfold.bootstrap.json");
        var cfg = TestSupport.MakeConfig(tweak: c =>
        {
            c.Deployment.Hosts = ["api.example.com"];
            c.Deployment.OutputDir = tmpDir;
        });
        await File.WriteAllTextAsync(
            configPath,
            JsonSerializer.Serialize(cfg, BootstrapJsonContext.Default.BootstrapConfig));

        var options = TestSupport.MakeOptions(
            command: BootstrapCommand.Bootstrap,
            outputDir: tmpDir,
            configPath: configPath,
            skipPrereqs: true,
            nonInteractive: true,
            reconfigure: true);
        var logger = new PhaseLogger(options);

        await Assert.That(async () =>
                await ConfigPhase.RunAsync(options, logger, CancellationToken.None))
            .Throws<InvalidOperationException>()
            .WithMessageMatching("*--reconfigure*")
            .And.WithMessageMatching("*interactive*");
    }

    [Test]
    public async Task ExistingFileWithoutReconfigure_LoadsWithoutPrompt()
    {
        using var scratch = TestSupport.NewScratchDir("interfold-reconfigure-load");
        var tmpDir = scratch.Path;
        var configPath = Path.Combine(tmpDir, "interfold.bootstrap.json");
        var cfg = TestSupport.MakeConfig(tweak: c =>
        {
            c.Deployment.Hosts = ["loaded.example.com"];
            c.Deployment.OutputDir = tmpDir;
            c.Deployment.RootCaName = "Loaded Root CA";
        });
        await File.WriteAllTextAsync(
            configPath,
            JsonSerializer.Serialize(cfg, BootstrapJsonContext.Default.BootstrapConfig));

        var options = TestSupport.MakeOptions(
            command: BootstrapCommand.Bootstrap,
            outputDir: tmpDir,
            configPath: configPath,
            skipPrereqs: true,
            nonInteractive: true,
            reconfigure: false);
        var logger = new PhaseLogger(options);

        var loaded = await ConfigPhase.RunAsync(options, logger, CancellationToken.None);

        await Assert.That(loaded.Deployment.Hosts).IsEquivalentTo(["loaded.example.com"]);
        await Assert.That(loaded.Deployment.RootCaName).IsEqualTo("Loaded Root CA");
    }
}
