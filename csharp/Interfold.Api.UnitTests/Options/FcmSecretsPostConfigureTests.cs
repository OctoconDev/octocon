using Interfold.Api.Services.Secrets;
using Interfold.Contracts.Configuration;
using Interfold.Contracts.Secrets;
using Microsoft.Extensions.Options;
using TUnit.Mocks;

namespace Interfold.Api.UnitTests.Options;

/// <summary>
/// Locks the contract of <see cref="FcmSecretsPostConfigure"/>: the optional
/// <c>fcm:service_account_json</c> row is copied verbatim onto
/// <see cref="FcmConfiguration.ServiceAccountJson"/>, a missing row leaves it null (the
/// <c>IFCMService</c> DI factory falls back to <c>NullFCMService</c>), and only the default
/// named options bucket is patched.
/// </summary>
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
