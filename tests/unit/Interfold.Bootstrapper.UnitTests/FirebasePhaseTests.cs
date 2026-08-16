using System.Text.Json;
using Interfold.Bootstrapper.Cli;
using Interfold.Bootstrapper.Configuration;
using Interfold.Bootstrapper.Phases;
using Interfold.Settings.Contracts.Configuration;

namespace Interfold.Bootstrapper.UnitTests;

/// <summary>
/// Pins the shape contract of <see cref="FirebasePhase"/>: which fields it extracts from
/// each Firebase input, the snake_case naming policy on the emitted seed JSON, and the
/// "empty path means skip" semantics for unconfigured platforms. Runs under
/// <c>JsonSerializerIsReflectionEnabledByDefault=false</c> so missing source-gen coverage
/// fails here the same way the PublishTrimmed ELF fails in production.
/// </summary>
public sealed class FirebasePhaseTests
{
    private static PhaseLogger Logger() => new(TestOptions("/tmp"));

    private static BootstrapOptions TestOptions(string outputDir) => new(
        Command: BootstrapCommand.Bootstrap,
        ConfigPath: null,
        OutputDir: outputDir,
        SkipPrereqs: true,
        RotateSecrets: false,
        RotateCerts: false,
        NonInteractive: true,
        FaultInject: null,
        PrintPhaseStatus: false);

    private static string NewTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "firebase-phase-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private const string AndroidFixture = /*lang=json,strict*/ """
    {
      "project_info": {
        "project_number": "111222333444",
        "project_id": "octocon-test",
        "storage_bucket": "octocon-test.appspot.com"
      },
      "client": [
        {
          "client_info": {
            "mobilesdk_app_id": "1:111222333444:android:abcdef1234567890",
            "android_client_info": { "package_name": "app.octocon.android" }
          },
          "api_key": [
            { "current_key": "AIzaSyTest-Android-Key" }
          ]
        }
      ]
    }
    """;

    private const string IosFixture = /*lang=xml*/ """
    <?xml version="1.0" encoding="UTF-8"?>
    <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
    <plist version="1.0">
    <dict>
        <key>API_KEY</key><string>AIzaSyTest-Ios-Key</string>
        <key>GOOGLE_APP_ID</key><string>1:111222333444:ios:1234567890abcdef</string>
        <key>GCM_SENDER_ID</key><string>111222333444</string>
        <key>PROJECT_ID</key><string>octocon-test</string>
        <key>STORAGE_BUCKET</key><string>octocon-test.appspot.com</string>
        <key>BUNDLE_ID</key><string>app.octocon.ios</string>
        <key>CLIENT_ID</key><string>111222333444-abc.apps.googleusercontent.com</string>
        <key>IS_ADS_ENABLED</key><false/>
    </dict>
    </plist>
    """;

    private const string WebFixture = /*lang=json,strict*/ """
    {
      "apiKey": "AIzaSyTest-Web-Key",
      "authDomain": "octocon-test.firebaseapp.com",
      "projectId": "octocon-test",
      "storageBucket": "octocon-test.appspot.com",
      "messagingSenderId": "111222333444",
      "appId": "1:111222333444:web:1234567890abcdef",
      "vapidKey": "BOx-test-vapid-key"
    }
    """;

    private const string ServiceAccountFixture = /*lang=json,strict*/ """
    {
      "type": "service_account",
      "project_id": "octocon-test",
      "private_key_id": "abc123",
      "private_key": "-----BEGIN PRIVATE KEY-----\nfake\n-----END PRIVATE KEY-----\n",
      "client_email": "sa@octocon-test.iam.gserviceaccount.com",
      "client_id": "111222333444"
    }
    """;

    [Test]
    public async Task RunAsync_AllFourInputsPresent_ProducesFourNonNullSeedValues()
    {
        var dir = NewTempDir();
        try
        {
            var android = Path.Combine(dir, "google-services.json");
            var ios = Path.Combine(dir, "GoogleService-Info.plist");
            var web = Path.Combine(dir, "firebase-web-config.json");
            var sa = Path.Combine(dir, "sa.json");
            File.WriteAllText(android, AndroidFixture);
            File.WriteAllText(ios, IosFixture);
            File.WriteAllText(web, WebFixture);
            File.WriteAllText(sa, ServiceAccountFixture);

            var config = new BootstrapConfig
            {
                Firebase = new FirebaseSection
                {
                    AndroidConfigPath = android,
                    IosConfigPath = ios,
                    WebConfigPath = web,
                    ServiceAccountPath = sa,
                },
            };

            var result = await FirebasePhase.RunAsync(TestOptions(dir), config, Logger(), CancellationToken.None);

            await Assert.That(result.AndroidClientJson).IsNotNull();
            await Assert.That(result.IosClientJson).IsNotNull();
            await Assert.That(result.WebClientJson).IsNotNull();
            await Assert.That(result.ServiceAccountJson).IsNotNull();
        }
        finally { TryDelete(dir); }
    }

    [Test]
    public async Task AndroidJson_RoundTripsIntoFirebaseAndroidClientConfig()
    {
        var dir = NewTempDir();
        try
        {
            var android = Path.Combine(dir, "gs.json");
            File.WriteAllText(android, AndroidFixture);

            var config = new BootstrapConfig
            {
                Firebase = new FirebaseSection { AndroidConfigPath = android },
            };
            var result = await FirebasePhase.RunAsync(TestOptions(dir), config, Logger(), CancellationToken.None);

            var parsed = JsonSerializer.Deserialize(
                result.AndroidClientJson!,
                FirebaseSnakeCaseWriteContext.Default.FirebaseAndroidClientConfig)!;
            await Assert.That(parsed).IsNotNull()
                .Because("The bootstrapper's emitted JSON must deserialise cleanly into the API's typed record under the same snake_case options SecretsBootstrapService uses.");
            await Assert.That(parsed.ApiKey).IsEqualTo("AIzaSyTest-Android-Key");
            await Assert.That(parsed.ApplicationId).IsEqualTo("1:111222333444:android:abcdef1234567890");
            await Assert.That(parsed.ProjectId).IsEqualTo("octocon-test");
            await Assert.That(parsed.GcmSenderId).IsEqualTo("111222333444");
            await Assert.That(parsed.StorageBucket).IsEqualTo("octocon-test.appspot.com");
        }
        finally { TryDelete(dir); }
    }

    [Test]
    public async Task IosPlist_RoundTripsIntoFirebaseIosClientConfig()
    {
        var dir = NewTempDir();
        try
        {
            var ios = Path.Combine(dir, "gs.plist");
            File.WriteAllText(ios, IosFixture);

            var config = new BootstrapConfig
            {
                Firebase = new FirebaseSection { IosConfigPath = ios },
            };
            var result = await FirebasePhase.RunAsync(TestOptions(dir), config, Logger(), CancellationToken.None);

            var parsed = JsonSerializer.Deserialize(
                result.IosClientJson!,
                FirebaseSnakeCaseWriteContext.Default.FirebaseIosClientConfig)!;
            await Assert.That(parsed).IsNotNull();
            await Assert.That(parsed.ApiKey).IsEqualTo("AIzaSyTest-Ios-Key");
            await Assert.That(parsed.GoogleAppId).IsEqualTo("1:111222333444:ios:1234567890abcdef");
            await Assert.That(parsed.GcmSenderId).IsEqualTo("111222333444");
            await Assert.That(parsed.ProjectId).IsEqualTo("octocon-test");
            await Assert.That(parsed.StorageBucket).IsEqualTo("octocon-test.appspot.com");
            await Assert.That(parsed.BundleId).IsEqualTo("app.octocon.ios");
            await Assert.That(parsed.ClientId).IsEqualTo("111222333444-abc.apps.googleusercontent.com");
        }
        finally { TryDelete(dir); }
    }

    [Test]
    public async Task WebJson_AcceptsCamelCaseInput_AndRoundTripsIntoFirebaseWebClientConfig()
    {
        var dir = NewTempDir();
        try
        {
            var web = Path.Combine(dir, "web.json");
            File.WriteAllText(web, WebFixture);

            var config = new BootstrapConfig
            {
                Firebase = new FirebaseSection { WebConfigPath = web },
            };
            var result = await FirebasePhase.RunAsync(TestOptions(dir), config, Logger(), CancellationToken.None);

            var parsed = JsonSerializer.Deserialize(
                result.WebClientJson!,
                FirebaseSnakeCaseWriteContext.Default.FirebaseWebClientConfig)!;
            await Assert.That(parsed).IsNotNull()
                .Because("The camelCase console output must be normalised into snake_case so the API's SnakeCaseLower deserialiser accepts the seeded row.");
            await Assert.That(parsed.ApiKey).IsEqualTo("AIzaSyTest-Web-Key");
            await Assert.That(parsed.AuthDomain).IsEqualTo("octocon-test.firebaseapp.com");
            await Assert.That(parsed.ProjectId).IsEqualTo("octocon-test");
            await Assert.That(parsed.StorageBucket).IsEqualTo("octocon-test.appspot.com");
            await Assert.That(parsed.MessagingSenderId).IsEqualTo("111222333444");
            await Assert.That(parsed.AppId).IsEqualTo("1:111222333444:web:1234567890abcdef");
            await Assert.That(parsed.VapidKey).IsEqualTo("BOx-test-vapid-key");
        }
        finally { TryDelete(dir); }
    }

    [Test]
    public async Task ServiceAccount_IsPassedThroughVerbatim()
    {
        var dir = NewTempDir();
        try
        {
            var sa = Path.Combine(dir, "sa.json");
            File.WriteAllText(sa, ServiceAccountFixture);

            var config = new BootstrapConfig
            {
                Firebase = new FirebaseSection { ServiceAccountPath = sa },
            };
            var result = await FirebasePhase.RunAsync(TestOptions(dir), config, Logger(), CancellationToken.None);

            await Assert.That(result.ServiceAccountJson).IsEqualTo(ServiceAccountFixture)
                .Because("The FCM SDK's GoogleCredential.FromJson consumes the file verbatim; any reshaping here would invalidate the credential's signature.");
        }
        finally { TryDelete(dir); }
    }

    [Test]
    public async Task EmptyPaths_ReturnAllNullsAndProduceNoSeedRows()
    {
        var dir = NewTempDir();
        try
        {
            var config = new BootstrapConfig
            {
                Firebase = new FirebaseSection(), // all four defaults are ""
            };
            var result = await FirebasePhase.RunAsync(TestOptions(dir), config, Logger(), CancellationToken.None);

            await Assert.That(result.AndroidClientJson).IsNull();
            await Assert.That(result.IosClientJson).IsNull();
            await Assert.That(result.WebClientJson).IsNull();
            await Assert.That(result.ServiceAccountJson).IsNull();
        }
        finally { TryDelete(dir); }
    }

    [Test]
    public async Task NonExistentPath_ThrowsFileNotFound()
    {
        var dir = NewTempDir();
        try
        {
            var config = new BootstrapConfig
            {
                Firebase = new FirebaseSection
                {
                    AndroidConfigPath = Path.Combine(dir, "does-not-exist.json"),
                },
            };
            await Assert.That(async () =>
                await FirebasePhase.RunAsync(TestOptions(dir), config, Logger(), CancellationToken.None))
                .Throws<FileNotFoundException>()
                .Because("A typo in the path must fail loudly at bootstrap time rather than silently disabling push.");
        }
        finally { TryDelete(dir); }
    }

    [Test]
    public async Task MalformedServiceAccount_MissingTypeField_Throws()
    {
        var dir = NewTempDir();
        try
        {
            var sa = Path.Combine(dir, "sa.json");
            // Well-formed JSON but not a service-account credential — the phase should reject it
            // rather than seeding an invalid credential that would explode at first FCM send.
            File.WriteAllText(sa, "{\"foo\":\"bar\"}");
            var config = new BootstrapConfig
            {
                Firebase = new FirebaseSection { ServiceAccountPath = sa },
            };
            await Assert.That(async () =>
                await FirebasePhase.RunAsync(TestOptions(dir), config, Logger(), CancellationToken.None))
                .Throws<InvalidDataException>();
        }
        finally { TryDelete(dir); }
    }

    [Test]
    public async Task RelativePath_ResolvesAgainstOutputDir()
    {
        var dir = NewTempDir();
        try
        {
            var subDir = Path.Combine(dir, "firebase");
            Directory.CreateDirectory(subDir);
            var android = Path.Combine(subDir, "gs.json");
            File.WriteAllText(android, AndroidFixture);

            var config = new BootstrapConfig
            {
                Firebase = new FirebaseSection
                {
                    // Relative path — must resolve under the phase's outputDir.
                    AndroidConfigPath = Path.Combine("firebase", "gs.json"),
                },
            };
            var result = await FirebasePhase.RunAsync(TestOptions(dir), config, Logger(), CancellationToken.None);
            await Assert.That(result.AndroidClientJson).IsNotNull();
        }
        finally { TryDelete(dir); }
    }

    private static void TryDelete(string dir) => TestSupport.TryDeleteDir(dir);
}
