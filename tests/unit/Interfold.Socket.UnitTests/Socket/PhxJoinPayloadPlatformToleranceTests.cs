using System.Text.Json;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;
using Interfold.Socket.Contracts;

namespace Interfold.Api.UnitTests.Socket;

// Join-side tolerance for PhxJoinPayload.Platform: an unknown wire spelling round-trips
// to null (never throws JsonException) and every sibling member survives. Regression
// guard for Api_UserSocketEndpoint_AllowsWebSocketUpgrade — pre-fix, platform="wasm"
// threw and the handler's outer catch substituted a defaulted payload, wiping the token
// and answering the join with Unauthorized.
public sealed class PhxJoinPayloadPlatformToleranceTests
{
    [Test]
    [Arguments("wasm")]
    [Arguments("WASM")]
    [Arguments("desktop")]
    [Arguments("")]
    [Arguments("   ")]
    public async Task Deserialize_WithUnknownPlatformString_ReturnsNullPlatform_AndPreservesSiblings(string platform)
    {
        var json = $$"""
        {
          "token": "eyJhbGciOiJFUzI1NiIsInR5cCI6IkpXVCJ9.payload.signature",
          "protocolVersion": "2.0.0",
          "platform": "{{platform}}",
          "isReconnect": true,
          "forceBatch": false
        }
        """;

        var payload = JsonSerializer.Deserialize<PhxJoinPayload>(json, SocketJson.Options);

        using (Assert.Multiple())
        {
            await Assert.That(payload).IsNotNull().Because("Expected the deserialize to succeed even with an unknown platform value.");
            await Assert.That(payload!.Platform).IsNull().Because($"Expected platform='{platform}' to round-trip to null (tolerant contract), not throw or map to a wrong-vocabulary member.");
            await Assert.That(payload.Token).IsEqualTo(new SocketToken("eyJhbGciOiJFUzI1NiIsInR5cCI6IkpXVCJ9.payload.signature"))
                .Because("Regression: an unknown platform value must NOT wipe the sibling token — that was the exact regression this test guards against (Api_UserSocketEndpoint_AllowsWebSocketUpgrade).");
            await Assert.That(payload.ProtocolVersion).IsEqualTo("2.0.0").Because("Expected the sibling protocolVersion to survive the unknown-platform deserialize.");
            await Assert.That(payload.IsReconnect).IsTrue().Because("Expected the sibling isReconnect to survive the unknown-platform deserialize.");
            await Assert.That(payload.ForceBatch).IsFalse().Because("Expected the sibling forceBatch to survive the unknown-platform deserialize.");
        }
    }

    [Test]
    [Arguments("android", ClientPlatform.Android)]
    [Arguments("ANDROID", ClientPlatform.Android)]
    [Arguments("ios", ClientPlatform.Ios)]
    [Arguments("Ios",  ClientPlatform.Ios)]
    [Arguments("web",  ClientPlatform.Web)]
    public async Task Deserialize_WithKnownPlatformSpelling_MapsToEnumMember(string platform, ClientPlatform expected)
    {
        var json = $$"""
        {
          "token": "t",
          "platform": "{{platform}}"
        }
        """;

        var payload = JsonSerializer.Deserialize<PhxJoinPayload>(json, SocketJson.Options);

        await Assert.That(payload!.Platform).IsEqualTo(expected)
            .Because($"Expected platform='{platform}' to map to {expected} — tolerant read must not weaken known wire spellings.");
    }

    [Test]
    public async Task Deserialize_WithMissingPlatform_LeavesPlatformNull()
    {
        var json = """
        {
          "token": "t",
          "isReconnect": false
        }
        """;

        var payload = JsonSerializer.Deserialize<PhxJoinPayload>(json, SocketJson.Options);

        await Assert.That(payload!.Platform).IsNull()
            .Because("Expected a missing platform member to stay null so the join branch treats it as 'not iOS' (legacy semantic).");
    }

    [Test]
    public async Task Deserialize_WithNumericPlatform_ReturnsNull_WithoutThrowing()
    {
        var json = """
        {
          "token": "t",
          "platform": 42
        }
        """;

        var payload = JsonSerializer.Deserialize<PhxJoinPayload>(json, SocketJson.Options);

        await Assert.That(payload!.Platform).IsNull()
            .Because("Expected a non-string platform token to be tolerated as null (belt-and-braces beyond the wasm case).");
    }

    [Test]
    [Arguments(ClientPlatform.Android, "android")]
    [Arguments(ClientPlatform.Ios,     "ios")]
    [Arguments(ClientPlatform.Web,     "web")]
    public async Task Serialize_KnownPlatform_EmitsCanonicalLowercaseWireSpelling(ClientPlatform value, string expectedWire)
    {
        var payload = new PhxJoinPayload { Token = new SocketToken("t"), Platform = value };

        var json = JsonSerializer.Serialize(payload, SocketJson.Options);

        await Assert.That(json).Contains($"\"platform\":\"{expectedWire}\"")
            .Because($"Expected known platform {value} to serialize as the canonical wire spelling '{expectedWire}'.");
    }
}
