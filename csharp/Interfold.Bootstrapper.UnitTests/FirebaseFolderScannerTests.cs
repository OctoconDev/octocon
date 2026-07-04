using Interfold.Bootstrapper.Configuration;

namespace Interfold.Bootstrapper.UnitTests;

/// <summary>
/// Unit tests for <see cref="FirebaseFolderScanner"/>. Every case drives the scanner
/// against a fresh temp directory containing a synthetic subset of the four Firebase
/// artefacts and asserts both the resolved <see cref="FirebaseSection"/> paths and the
/// <see cref="FirebaseFolderScanResult.Missing"/> platform list — the two pieces of
/// output the interactive wizard's summary table consumes.
/// </summary>
public sealed class FirebaseFolderScannerTests
{
    /// <summary>
    /// Minimal but real-shaped service-account JSON. Only the <c>type</c> field is what
    /// the scanner's content-sniff branch reads; the surrounding fields exist so the
    /// fixture reads as a plausible service account for anyone opening the file during
    /// a test debug session.
    /// </summary>
    private const string ServiceAccountFixture = /*lang=json,strict*/ """
    {
      "type": "service_account",
      "project_id": "octocon-test",
      "private_key_id": "abc123",
      "private_key": "-----BEGIN PRIVATE KEY-----\nfake\n-----END PRIVATE KEY-----\n",
      "client_email": "firebase-adminsdk@octocon-test.iam.gserviceaccount.com",
      "client_id": "111",
      "auth_uri": "https://accounts.google.com/o/oauth2/auth",
      "token_uri": "https://oauth2.googleapis.com/token"
    }
    """;

    private static string NewTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "firebase-scanner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    [Test]
    public async Task ScanFindsAllFourCanonicalFiles()
    {
        var folder = NewTempDir();
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
        // Only Android + service-account present — the other two platforms should be
        // reported in the Missing list so the wizard's summary can point the operator
        // at "Configure per-file" for the rest.
        var folder = NewTempDir();
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
        // Both a glob-hit file and a content-sniff-eligible file are present. The glob
        // hit is cheaper (no parse), matches Google's default download name, and must
        // win — otherwise operators who kept both the original download and a renamed
        // copy would get non-deterministic scanner behaviour.
        var folder = NewTempDir();
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
        // No file matches the *-firebase-adminsdk-*.json glob; the scanner must open
        // each *.json in turn and pick the one whose root object has type = service_account.
        var folder = NewTempDir();
        var renamed = Path.Combine(folder, "renamed-service-account.json");
        File.WriteAllText(renamed, ServiceAccountFixture);
        File.WriteAllText(Path.Combine(folder, "not-a-service-account.json"), """{"foo":"bar"}""");

        var result = FirebaseFolderScanner.Scan(folder);

        await Assert.That(result.Section.ServiceAccountPath).IsEqualTo(Path.GetFullPath(renamed));
    }

    [Test]
    public async Task ScanSkipsUnrelatedJsonFilesInSniffFallback()
    {
        // No canonical android/ios/web files, no glob-match service account, and every
        // *.json in the folder is unrelated. The scanner must return without crashing
        // and Missing must include all four platform labels.
        var folder = NewTempDir();
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
        // A malformed *.json file in the folder must not abort the scan — the scanner is
        // a discovery helper, not a validator, and one broken file shouldn't hide a
        // valid service account sitting alongside it.
        var folder = NewTempDir();
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
