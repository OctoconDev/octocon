using Interfold.Api.UnitTests.Support;
using Interfold.Settings.Api.Services.Secrets;
using Interfold.Settings.Contracts.Configuration;
using Interfold.Shared.Contracts.Secrets;

namespace Interfold.Api.UnitTests.Options;

// Contract for FcmSecretsPostConfigure: fcm:service_account_json flows verbatim onto
// FcmConfiguration.ServiceAccountJson; a missing row leaves it null (IFCMService
// falls back to NullFCMService); only the default named bucket is patched.
public sealed class FcmSecretsPostConfigureTests
{
    private const string ServiceAccountJson = """{"type":"service_account","project_id":"test"}""";

    [Test]
    public async Task PostConfigure_RowPresent_PopulatesServiceAccountJson()
    {
        var options = SecretsSnapshotMock.Empty()
            .With(SecretsStoreKeys.FcmServiceAccountJson, ServiceAccountJson)
            .ApplyPostConfigure<FcmSecretsPostConfigure, FcmConfiguration>();

        await Assert.That(options.ServiceAccountJson).IsEqualTo(ServiceAccountJson)
            .Because("The fcm:service_account_json row must flow through verbatim — the Firebase Admin SDK, not this patcher, is responsible for parsing it.");
    }

    [Test]
    public async Task PostConfigure_MissingRow_LeavesNull()
    {
        var options = SecretsSnapshotMock.Empty()
            .ApplyPostConfigure<FcmSecretsPostConfigure, FcmConfiguration>();

        await Assert.That(options.ServiceAccountJson).IsNull()
            .Because("FCM is opt-in per deployment — an absent row must leave ServiceAccountJson null so the IFCMService DI factory falls back to NullFCMService instead of tripping a boot-time validator.");
    }

    [Test]
    public async Task PostConfigure_NamedInstance_LeavesUntouched()
    {
        var options = SecretsSnapshotMock.Empty()
            .With(SecretsStoreKeys.FcmServiceAccountJson, ServiceAccountJson)
            .ApplyPostConfigure<FcmSecretsPostConfigure, FcmConfiguration>(name: "other-name");

        await Assert.That(options.ServiceAccountJson).IsNull()
            .Because("PostConfigure guards on Options.DefaultName so a named bucket never receives the default's FCM credential.");
    }

}
