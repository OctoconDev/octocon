using Interfold.Shared.Contracts;
using System.Net;
using System.Text.Json;
using Interfold.Api.Helpers;
using Interfold.Api.Models;
using Interfold.Shared.Contracts.Configuration;
using Microsoft.AspNetCore.Http;

namespace Interfold.Api.UnitTests.Helpers;

// FirebaseConfigResolver: three response shapes, two error cases, cache-header stamping,
// and ETag stability — the wire contract mobile / wasm clients depend on.
public sealed class FirebaseConfigResolverTests
{
    private static FirebaseClientConfiguration FullyPopulated() => new()
    {
        Android = new FirebaseAndroidClientConfig(
            ApiKey: "AIzaTEST-Android",
            ApplicationId: "1:1234567890:android:abcdef",
            ProjectId: "octocon-test",
            GcmSenderId: "1234567890",
            StorageBucket: "octocon-test.appspot.com"),
        Ios = new FirebaseIosClientConfig(
            ApiKey: "AIzaTEST-Ios",
            GoogleAppId: "1:1234567890:ios:fedcba",
            GcmSenderId: "1234567890",
            ProjectId: "octocon-test",
            StorageBucket: "octocon-test.appspot.com",
            BundleId: "app.octocon.ios",
            ClientId: "1234567890-abc.apps.googleusercontent.com"),
        Web = new FirebaseWebClientConfig(
            ApiKey: "AIzaTEST-Web",
            AuthDomain: "octocon-test.firebaseapp.com",
            ProjectId: "octocon-test",
            StorageBucket: "octocon-test.appspot.com",
            MessagingSenderId: "1234567890",
            AppId: "1:1234567890:web:123abc",
            VapidKey: "BOx-test-vapid-key"),
    };

    [Test]
    public async Task Resolve_Android_HappyPath_ProducesAndroidResponseWithMatchingFields()
    {
        var (payload, error) = FirebaseConfigResolver.Resolve(FullyPopulated(), "android");

        await Assert.That(error).IsNull();
        await Assert.That(payload).IsTypeOf<FirebaseAndroidConfigResponse>();
        var android = (FirebaseAndroidConfigResponse)payload!;
        await Assert.That(android.ApiKey).IsEqualTo("AIzaTEST-Android");
        await Assert.That(android.ApplicationId).IsEqualTo("1:1234567890:android:abcdef");
        await Assert.That(android.ProjectId).IsEqualTo("octocon-test");
        await Assert.That(android.GcmSenderId).IsEqualTo("1234567890");
        await Assert.That(android.StorageBucket).IsEqualTo("octocon-test.appspot.com");
    }

    [Test]
    public async Task Resolve_Ios_HappyPath_ProducesIosResponseWithMatchingFields()
    {
        var (payload, error) = FirebaseConfigResolver.Resolve(FullyPopulated(), "ios");

        await Assert.That(error).IsNull();
        await Assert.That(payload).IsTypeOf<FirebaseIosConfigResponse>();
        var ios = (FirebaseIosConfigResponse)payload!;
        await Assert.That(ios.ApiKey).IsEqualTo("AIzaTEST-Ios");
        await Assert.That(ios.GoogleAppId).IsEqualTo("1:1234567890:ios:fedcba");
        await Assert.That(ios.BundleId).IsEqualTo("app.octocon.ios");
        await Assert.That(ios.ClientId).IsEqualTo("1234567890-abc.apps.googleusercontent.com");
    }

    [Test]
    public async Task Resolve_Web_HappyPath_ProducesWebResponseWithMatchingFields()
    {
        var (payload, error) = FirebaseConfigResolver.Resolve(FullyPopulated(), "web");

        await Assert.That(error).IsNull();
        await Assert.That(payload).IsTypeOf<FirebaseWebConfigResponse>();
        var web = (FirebaseWebConfigResponse)payload!;
        await Assert.That(web.VapidKey).IsEqualTo("BOx-test-vapid-key")
            .Because("The VAPID key is the one field unique to the web variant — must round-trip so getToken({ vapidKey }) works on the client.");
        await Assert.That(web.MessagingSenderId).IsEqualTo("1234567890");
    }

    [Test]
    public async Task Resolve_PlatformCasingIsNormalised()
    {
        var (payload, error) = FirebaseConfigResolver.Resolve(FullyPopulated(), "  ANDROID  ");
        await Assert.That(error).IsNull();
        await Assert.That(payload).IsTypeOf<FirebaseAndroidConfigResponse>();
    }

    [Test]
    public async Task Resolve_MissingPlatformConfig_Returns503WithUnavailableCode()
    {
        var partial = new FirebaseClientConfiguration
        {
            Android = FullyPopulated().Android,
        };
        var (payload, error) = FirebaseConfigResolver.Resolve(partial, "ios");

        await Assert.That(payload).IsNull();
        await Assert.That(error).IsNotNull();
        await Assert.That(error!.Code).IsEqualTo(ErrorCodes.FirebaseConfigUnavailable);
        await Assert.That(error.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
    }

    [Test]
    public async Task Resolve_UnknownPlatform_Returns400WithInvalidPlatformCode()
    {
        var (payload, error) = FirebaseConfigResolver.Resolve(FullyPopulated(), "windows");

        await Assert.That(payload).IsNull();
        await Assert.That(error).IsNotNull();
        await Assert.That(error!.Code).IsEqualTo(ErrorCodes.InvalidPlatform);
        await Assert.That(error.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task Resolve_NullPlatform_Returns400WithInvalidPlatformCode()
    {
        var (payload, error) = FirebaseConfigResolver.Resolve(FullyPopulated(), null);

        await Assert.That(payload).IsNull();
        await Assert.That(error).IsNotNull();
        await Assert.That(error!.Code).IsEqualTo(ErrorCodes.InvalidPlatform);
    }

    [Test]
    public async Task ApplyCacheHeaders_SetsFiveMinuteMaxAge()
    {
        var response = BuildResponse();
        var payload = (FirebaseClientConfigResponse)new FirebaseAndroidConfigResponse(
            "k", "a", "p", "s", null);

        FirebaseConfigResolver.ApplyCacheHeaders(response, payload);

        await Assert.That(response.Headers["Cache-Control"].ToString())
            .IsEqualTo("public, max-age=300, must-revalidate")
            .Because("Server-side ceiling of 5 minutes bounds how quickly a Firebase rotation propagates through the wasm service worker's install-time fetch.");
    }

    [Test]
    public async Task ApplyCacheHeaders_EtagIsQuotedHex()
    {
        var response = BuildResponse();
        var payload = (FirebaseClientConfigResponse)new FirebaseAndroidConfigResponse(
            "k", "a", "p", "s", null);

        FirebaseConfigResolver.ApplyCacheHeaders(response, payload);
        var etag = response.Headers.ETag.ToString();

        await Assert.That(etag).StartsWith("\"");
        await Assert.That(etag).EndsWith("\"");
        await Assert.That(etag.Trim('"').Length).IsEqualTo(64)
            .Because("SHA-256 uppercase hex is 64 characters — matches Convert.ToHexString on a 32-byte digest.");
    }

    [Test]
    public async Task ApplyCacheHeaders_IdenticalPayloadsProduceIdenticalEtags()
    {
        var payloadA = (FirebaseClientConfigResponse)new FirebaseWebConfigResponse(
            "k", "d", "p", "s", "sender", "app", "vapid");
        var payloadB = (FirebaseClientConfigResponse)new FirebaseWebConfigResponse(
            "k", "d", "p", "s", "sender", "app", "vapid");

        var responseA = BuildResponse();
        var responseB = BuildResponse();
        FirebaseConfigResolver.ApplyCacheHeaders(responseA, payloadA);
        FirebaseConfigResolver.ApplyCacheHeaders(responseB, payloadB);

        await Assert.That(responseA.Headers.ETag.ToString())
            .IsEqualTo(responseB.Headers.ETag.ToString());
    }

    [Test]
    public async Task ApplyCacheHeaders_DifferentPayloadsProduceDifferentEtags()
    {
        var payloadA = (FirebaseClientConfigResponse)new FirebaseAndroidConfigResponse(
            "k1", "a", "p", "s", null);
        var payloadB = (FirebaseClientConfigResponse)new FirebaseAndroidConfigResponse(
            "k2", "a", "p", "s", null);

        var responseA = BuildResponse();
        var responseB = BuildResponse();
        FirebaseConfigResolver.ApplyCacheHeaders(responseA, payloadA);
        FirebaseConfigResolver.ApplyCacheHeaders(responseB, payloadB);

        await Assert.That(responseA.Headers.ETag.ToString())
            .IsNotEqualTo(responseB.Headers.ETag.ToString());
    }

    // Regression guard on the discriminator + field-name policy — a flipped naming
    // policy or removed [JsonPolymorphic] annotation would break the kotlinx decoder.
    [Test]
    public async Task WireContract_UsesSnakeCaseFieldNames()
    {
        var payload = (FirebaseClientConfigResponse)new FirebaseWebConfigResponse(
            "aaa", "bbb.example.com", "ccc", null, "ddd", "eee", "fff");
        var json = JsonSerializer.Serialize(payload, FirebaseConfigResolver.EtagJsonOptions);

        await Assert.That(json).Contains("\"type\":\"web\"");
        await Assert.That(json).Contains("\"api_key\":\"aaa\"");
        await Assert.That(json).Contains("\"auth_domain\":\"bbb.example.com\"");
        await Assert.That(json).Contains("\"vapid_key\":\"fff\"");
    }

    private static HttpResponse BuildResponse() => new DefaultHttpContext().Response;
}
