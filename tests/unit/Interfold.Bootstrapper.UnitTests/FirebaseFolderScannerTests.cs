using Interfold.Bootstrapper.Configuration;

namespace Interfold.Bootstrapper.UnitTests;

public sealed class FirebaseFolderScannerTests
{
    private const string ServiceAccountFixture = """
    {
      "type": "service_account",
      "project_id": "octocon-test",
      "private_key_id": "abc123xyz",
      "private_key": "-----BEGIN PRIVATE KEY-----\nfake\n-----END PRIVATE KEY-----\n",
      "client_email": "firebase-adminsdk@octocon-test.iam.gserviceaccount.com",
      "client_id": "111",
      "auth_uri": "https://accounts.google.com/o/oauth2/auth",
      "token_uri": "https://oauth2.googleapis.com/token"
    }
    """;

    [Test]
    public async Task ScanFindsAllFourCanonicalFiles()
    {
        using var scratch = TestSupport.NewScratchDir("firebase-scanner");
        var folder = scratch.Path;
        File.WriteAllText(Path.Combine(folder, "google-services.json"), "{}");
        File.WriteAllText(Path.Combine(folder, "GoogleService-Info.plist"), "<plist/>");
        File.WriteAllText(Path.Combine(folder, "firebase-web-config.json"), "{}");
        File.WriteAllText(Path.Combine(folder, "octocon-firebase-adminsdk-abc123.json"), ServiceAccountFixture);

        var result = FirebaseFolderScanner.Scan(folder);

        await Assert.That(result.Section.AndroidConfigPath)
            .IsEqualTo(Path.GetFullPath(Path.Combine(folder, "google-services.json")));
        await Assert.That(result.Section.IosConfigPath)
            .IsEqualTo(Path.GetFullPath(Path.Combine(folder, "GoogleService-Info.plist")));
        await Assert.That(result.Section.WebConfigPath)
            .IsEqualTo(Path.GetFullPath(Path.Combine(folder, "firebase-web-config.json")));
        await Assert.That(result.Section.ServiceAccountPath)
            .IsEqualTo(Path.GetFullPath(Path.Combine(folder, "octocon-firebase-adminsdk-abc123.json")));
        await Assert.That(result.Missing).IsEmpty();
    }

    [Test]
    public async Task ScanReportsMissingPlatformsForPartialFolder()
    {
        using var scratch = TestSupport.NewScratchDir("firebase-scanner");
        var folder = scratch.Path;
        File.WriteAllText(Path.Combine(folder, "google-services.json"), "{}");
        File.WriteAllText(Path.Combine(folder, "myproject-firebase-adminsdk-xyz.json"), ServiceAccountFixture);

        var result = FirebaseFolderScanner.Scan(folder);

        await Assert.That(result.Section.AndroidConfigPath).IsNotEmpty();
        await Assert.That(result.Section.ServiceAccountPath).IsNotEmpty();
        await Assert.That(result.Section.IosConfigPath).IsEmpty();
        await Assert.That(result.Section.WebConfigPath).IsEmpty();
        await Assert.That(result.Missing).IsEquivalentTo(new[] { "ios", "web" });
    }

    [Test]
    public async Task ScanPrefersGlobMatchForServiceAccount()
    {
        using var scratch = TestSupport.NewScratchDir("firebase-scanner");
        var folder = scratch.Path;
        var globPath = Path.Combine(folder, "octocon-firebase-adminsdk-abc.json");
        var sniffPath = Path.Combine(folder, "renamed-service-account.json");
        File.WriteAllText(globPath, ServiceAccountFixture);
        File.WriteAllText(sniffPath, ServiceAccountFixture);

        var result = FirebaseFolderScanner.Scan(folder);

        await Assert.That(result.Section.ServiceAccountPath).IsEqualTo(Path.GetFullPath(globPath));
    }

    [Test]
    public async Task ScanFallsBackToContentSniffWhenGlobMisses()
    {
        using var scratch = TestSupport.NewScratchDir("firebase-scanner");
        var folder = scratch.Path;
        var renamed = Path.Combine(folder, "renamed-service-account.json");
        File.WriteAllText(renamed, ServiceAccountFixture);
        File.WriteAllText(Path.Combine(folder, "not-a-service-account.json"), """{"foo":"bar"}""");

        var result = FirebaseFolderScanner.Scan(folder);

        await Assert.That(result.Section.ServiceAccountPath).IsEqualTo(Path.GetFullPath(renamed));
    }

    [Test]
    public async Task ScanSkipsUnrelatedJsonFilesInSniffFallback()
    {
        using var scratch = TestSupport.NewScratchDir("firebase-scanner");
        var folder = scratch.Path;
        File.WriteAllText(Path.Combine(folder, "eslintrc.json"), """{"rules":{}}""");
        File.WriteAllText(Path.Combine(folder, "tsconfig.json"), """{"compilerOptions":{}}""");

        var result = FirebaseFolderScanner.Scan(folder);

        await Assert.That(result.Section.AndroidConfigPath).IsEmpty();
        await Assert.That(result.Section.IosConfigPath).IsEmpty();
        await Assert.That(result.Section.WebConfigPath).IsEmpty();
        await Assert.That(result.Section.ServiceAccountPath).IsEmpty();
        await Assert.That(result.Missing).IsEquivalentTo(new[] { "android", "ios", "web", "service_account" });
    }

    [Test]
    public async Task ScanIgnoresUnparseableJsonInSniffFallback()
    {
        using var scratch = TestSupport.NewScratchDir("firebase-scanner");
        var folder = scratch.Path;
        File.WriteAllText(Path.Combine(folder, "broken.json"), "{ not json");
        var real = Path.Combine(folder, "real.json");
        File.WriteAllText(real, ServiceAccountFixture);

        var result = FirebaseFolderScanner.Scan(folder);

        await Assert.That(result.Section.ServiceAccountPath).IsEqualTo(Path.GetFullPath(real));
    }

    [Test]
    public async Task ScanThrowsWhenFolderDoesNotExist()
    {
        var missing = Path.Combine(Path.GetTempPath(), "firebase-scanner-missing-" + Guid.NewGuid().ToString("N"));

        await Assert.That(() => FirebaseFolderScanner.Scan(missing))
            .Throws<DirectoryNotFoundException>();
    }
}
