using System.Text.Json;
using Interfold.Bootstrapper.Cli;
using Interfold.Bootstrapper.Configuration;
using Interfold.Bootstrapper.Phases;
using TUnit.Core;

namespace Interfold.Bootstrapper.UnitTests;

/// <summary>
/// Covers the backfill logic in <see cref="SecretsPhase.RunAsync"/>. A pre-existing
/// <c>secrets.json</c> written by an older bootstrapper version may be missing keys that newer
/// phases require (e.g. <c>postgresInitPassword</c>, <c>postgresAdminPassword</c>,
/// <c>scyllaAdminPassword</c>, or the four JWT PEMs). The phase must detect the missing keys,
/// mint replacements, and persist the result while leaving other already-populated fields alone.
/// </summary>
public sealed class SecretsBackfillTests
{
    private static BootstrapOptions OptionsFor(string outputDir, bool rotateSecrets = false) =>
        TestSupport.MakeOptions(
            command: BootstrapCommand.Bootstrap,
            outputDir: outputDir,
            skipPrereqs: true,
            rotateSecrets: rotateSecrets,
            nonInteractive: true);

    /// <summary>Writes <paramref name="secrets"/> as JSON to the canonical location under <paramref name="outputDir"/>.</summary>
    private static async Task StagePriorSecretsAsync(string outputDir, GeneratedSecrets secrets)
    {
        var secretsDir = Path.Combine(outputDir, "secrets");
        Directory.CreateDirectory(secretsDir);
        var path = Path.Combine(secretsDir, "secrets.json");
        var json = JsonSerializer.Serialize(secrets, BootstrapJsonContext.Default.GeneratedSecrets);
        await File.WriteAllTextAsync(path, json);
    }

    private static async Task<GeneratedSecrets> LoadPersistedAsync(string outputDir)
    {
        var path = Path.Combine(outputDir, "secrets", "secrets.json");
        var json = await File.ReadAllTextAsync(path);
        return JsonSerializer.Deserialize(json, BootstrapJsonContext.Default.GeneratedSecrets)!;
    }

    private sealed record BackfillSetup(TestSupport.ScratchDir Scratch, GeneratedSecrets Prior, GeneratedSecrets Result) : IDisposable
    {
        public void Dispose() => Scratch.Dispose();
    }

    private static async Task<BackfillSetup> RunBackfillAsync(Action<GeneratedSecrets>? mutate = null, bool rotateSecrets = false)
    {
        var scratch = TestSupport.NewScratchDir("interfold-secrets");
        try
        {
            var prior = SecretsPhase.Generate();
            mutate?.Invoke(prior);
            await StagePriorSecretsAsync(scratch.Path, prior);

            var options = OptionsFor(scratch.Path, rotateSecrets);
            var logger = new PhaseLogger(options);
            var result = await SecretsPhase.RunAsync(options, new BootstrapConfig(), logger, CancellationToken.None);
            return new BackfillSetup(scratch, prior, result);
        }
        catch
        {
            scratch.Dispose();
            throw;
        }
    }

    [Test]
    public async Task BackfillsMissingPostgresInitPassword()
    {
        using var setup = await RunBackfillAsync(p => p.PostgresInitPassword = string.Empty);
        await Assert.That(setup.Result.PostgresInitPassword).IsNotEmpty();
        var reloaded = await LoadPersistedAsync(setup.Scratch.Path);
        await Assert.That(reloaded.PostgresInitPassword).IsEqualTo(setup.Result.PostgresInitPassword);
        await Assert.That(reloaded.PostgresPassword).IsEqualTo(setup.Prior.PostgresPassword);
    }

    [Test]
    public async Task BackfillsMissingPostgresAdminPassword()
    {
        using var setup = await RunBackfillAsync(p => p.PostgresAdminPassword = string.Empty);
        await Assert.That(setup.Result.PostgresAdminPassword).IsNotEmpty();
        var reloaded = await LoadPersistedAsync(setup.Scratch.Path);
        await Assert.That(reloaded.PostgresAdminPassword).IsEqualTo(setup.Result.PostgresAdminPassword);
        await Assert.That(reloaded.PostgresPassword).IsEqualTo(setup.Prior.PostgresPassword);
    }

    [Test]
    public async Task BackfillsMissingScyllaAdminPassword()
    {
        using var setup = await RunBackfillAsync(p => p.ScyllaAdminPassword = string.Empty);
        await Assert.That(setup.Result.ScyllaAdminPassword).IsNotEmpty();
        var reloaded = await LoadPersistedAsync(setup.Scratch.Path);
        await Assert.That(reloaded.ScyllaAdminPassword).IsEqualTo(setup.Result.ScyllaAdminPassword);
        await Assert.That(reloaded.ScyllaPassword).IsEqualTo(setup.Prior.ScyllaPassword);
    }

    [Test]
    public async Task RotateSecretsPreservesLeafPfxPassword()
    {
        using var setup = await RunBackfillAsync(p => p.LeafPfxPassword = "well-known-pfx-password-do-not-rotate-on-secrets-rotate", rotateSecrets: true);
        await Assert.That(setup.Result.LeafPfxPassword).IsEqualTo(setup.Prior.LeafPfxPassword);
        await Assert.That(setup.Result.PostgresPassword).IsNotEqualTo(setup.Prior.PostgresPassword);
        await Assert.That(setup.Result.ScyllaPassword).IsNotEqualTo(setup.Prior.ScyllaPassword);
    }

    [Test]
    public async Task RerunDoesNotEmitKeysDirectory()
    {
        using var setup = await RunBackfillAsync();
        var keysDir = Path.Combine(setup.Scratch.Path, "secrets", "keys");
        await Assert.That(Directory.Exists(keysDir)).IsFalse()
            .Because("JWT PEMs live in internal.secrets exclusively; no keys/ dir should appear.");
    }

    [Test]
    public async Task BackfillsMissingDeepLinkSecret()
    {
        using var setup = await RunBackfillAsync(p => p.DeepLinkSecret = string.Empty);
        await Assert.That(setup.Result.DeepLinkSecret).IsNotEmpty();
        var reloaded = await LoadPersistedAsync(setup.Scratch.Path);
        await Assert.That(reloaded.DeepLinkSecret).IsEqualTo(setup.Result.DeepLinkSecret);
        await Assert.That(reloaded.PostgresPassword).IsEqualTo(setup.Prior.PostgresPassword);
    }

    [Test]
    public async Task BackfillsMissingJwtRsaPrivatePem()
    {
        using var setup = await RunBackfillAsync(p => { p.JwtRsa256PrivateKeyPem = string.Empty; p.JwtRsa256PublicKeyPem = string.Empty; });
        await Assert.That(setup.Result.JwtRsa256PrivateKeyPem).Contains("-----BEGIN");
        await Assert.That(setup.Result.JwtRsa256PublicKeyPem).Contains("-----BEGIN");
        await Assert.That(setup.Result.JwtEs256PrivateKeyPem).IsEqualTo(setup.Prior.JwtEs256PrivateKeyPem);
    }

    [Test]
    public async Task BackfillsMissingJwtEs256PrivatePem()
    {
        using var setup = await RunBackfillAsync(p => { p.JwtEs256PrivateKeyPem = string.Empty; p.JwtEs256PublicKeyPem = string.Empty; });
        await Assert.That(setup.Result.JwtEs256PrivateKeyPem).Contains("-----BEGIN");
        await Assert.That(setup.Result.JwtEs256PublicKeyPem).Contains("-----BEGIN");
        await Assert.That(setup.Result.JwtRsa256PrivateKeyPem).IsEqualTo(setup.Prior.JwtRsa256PrivateKeyPem);
    }

    [Test]
    public async Task RotateSecretsRegeneratesJwtAndDeepLink()
    {
        using var setup = await RunBackfillAsync(rotateSecrets: true);
        await Assert.That(setup.Result.JwtRsa256PrivateKeyPem).IsNotEqualTo(setup.Prior.JwtRsa256PrivateKeyPem);
        await Assert.That(setup.Result.JwtEs256PrivateKeyPem).IsNotEqualTo(setup.Prior.JwtEs256PrivateKeyPem);
        await Assert.That(setup.Result.DeepLinkSecret).IsNotEqualTo(setup.Prior.DeepLinkSecret);
    }
}
