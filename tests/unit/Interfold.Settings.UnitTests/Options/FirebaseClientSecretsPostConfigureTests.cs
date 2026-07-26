using Interfold.Api.UnitTests.Support;
using Interfold.Settings.Api.Services.Secrets;
using Interfold.Settings.Contracts.Configuration;
using Interfold.Shared.Contracts.Secrets;

namespace Interfold.Api.UnitTests.Options;

// Contract for FirebaseClientSecretsPostConfigure: three optional JSON rows deserialise
// onto matching FirebaseClientConfiguration properties, a missing row leaves the
// platform null (endpoint returns 503), and a malformed row fails hard at boot.
public sealed class FirebaseClientSecretsPostConfigureTests
{
    private const string AndroidJson =
        """
        {
          "api_key": "AIzaFakeAndroidKey",
          "application_id": "1:1234567890:android:abcdef",
          "project_id": "test-project",
          "gcm_sender_id": "1234567890",
          "storage_bucket": "test-project.appspot.com"
        }
        """;

    private const string IosJson =
        """
        {
          "api_key": "AIzaFakeIosKey",
          "google_app_id": "1:1234567890:ios:abcdef",
          "gcm_sender_id": "1234567890",
          "project_id": "test-project",
          "storage_bucket": "test-project.appspot.com",
          "bundle_id": "app.octocon.ios",
          "client_id": "1234567890-abcdef.apps.googleusercontent.com"
        }
        """;

    private const string WebJson =
        """
        {
          "api_key": "AIzaFakeWebKey",
          "auth_domain": "test-project.firebaseapp.com",
          "project_id": "test-project",
          "storage_bucket": "test-project.appspot.com",
          "messaging_sender_id": "1234567890",
          "app_id": "1:1234567890:web:abcdef",
          "vapid_key": "TEST_VAPID_PUBLIC"
        }
        """;

    [Test]
    public async Task PostConfigure_ThreeValidPlatformJsonRows_PopulatesAll()
    {
        var options = SecretsSnapshotMock.Empty()
            .With(SecretsStoreKeys.FirebaseClientAndroid, AndroidJson)
            .With(SecretsStoreKeys.FirebaseClientIos,     IosJson)
            .With(SecretsStoreKeys.FirebaseClientWeb,     WebJson)
            .ApplyPostConfigure<FirebaseClientSecretsPostConfigure, FirebaseClientConfiguration>();

        using (Assert.Multiple())
        {
            await Assert.That(options.Android).IsNotNull()
                .Because("Android JSON row must deserialise into FirebaseAndroidClientConfig; a null here means the snake_case naming policy or the ParseOrThrow<T> helper broke.");
            await Assert.That(options.Android!.ApiKey).IsEqualTo("AIzaFakeAndroidKey")
                .Because("The snake_case api_key field must map to the PascalCase ApiKey property — verifies the JsonNamingPolicy.SnakeCaseLower binding on the deserializer options.");
            await Assert.That(options.Ios).IsNotNull()
                .Because("iOS JSON row parity with Android — same failure mode, distinct signal so a partial regression is easier to diagnose.");
            await Assert.That(options.Ios!.BundleId).IsEqualTo("app.octocon.ios")
                .Because("iOS-specific bundle_id must round-trip since the SDK cross-checks it against the running app's bundle at init.");
            await Assert.That(options.Web).IsNotNull()
                .Because("Web JSON row parity with Android/iOS.");
            await Assert.That(options.Web!.VapidKey).IsEqualTo("TEST_VAPID_PUBLIC")
                .Because("VAPID key is required for the JS SDK's getToken({vapidKey}) call — this field must survive PostConfigure.");
        }
    }

    // No [Required] on Firebase — empty snapshot passes validation; /api/settings/firebase-config
    // is what surfaces the missing seed as a 503 downstream.
    [Test]
    public async Task PostConfigure_MissingRows_LeavesAllNull()
    {
        var options = SecretsSnapshotMock.Empty()
            .ApplyPostConfigure<FirebaseClientSecretsPostConfigure, FirebaseClientConfiguration>();

        using (Assert.Multiple())
        {
            await Assert.That(options.Android).IsNull()
                .Because("Absent Android row must leave the property null so /api/settings/firebase-config?platform=android returns 503 rather than tripping a boot-time validator — Firebase is opt-in.");
            await Assert.That(options.Ios).IsNull()
                .Because("Absent iOS row parity — same optional-platform contract.");
            await Assert.That(options.Web).IsNull()
                .Because("Absent Web row parity — same optional-platform contract.");
        }
    }

    [Test]
    public async Task PostConfigure_MalformedJson_ThrowsWithKeyName()
    {
        var snapshot = SecretsSnapshotMock.Empty()
            .With(SecretsStoreKeys.FirebaseClientAndroid, "{ this is not JSON");
        var patcher = new FirebaseClientSecretsPostConfigure(snapshot.Object);
        var options = new FirebaseClientConfiguration();

        var ex = Assert.Throws<InvalidOperationException>(
            () => patcher.PostConfigure(Microsoft.Extensions.Options.Options.DefaultName, options));

        await Assert.That(ex!.Message)
            .Contains(SecretsStoreKeys.FirebaseClientAndroid.Value)
            .Because("The diagnostic message must name the offending internal.secrets row so an operator staring at the boot log can jump straight to the malformed seed.");
    }

    [Test]
    public async Task PostConfigure_NamedInstance_LeavesUntouched()
    {
        var options = SecretsSnapshotMock.Empty()
            .With(SecretsStoreKeys.FirebaseClientAndroid, AndroidJson)
            .ApplyPostConfigure<FirebaseClientSecretsPostConfigure, FirebaseClientConfiguration>(name: "other-name");

        await Assert.That(options.Android).IsNull()
            .Because("PostConfigure guards on Options.DefaultName so a named bucket never receives the default's Firebase payload.");
    }

}
