using System.Text.Json;
using Interfold.Contracts;
using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;

namespace Interfold.Api.UnitTests.Socket;

/// <summary>
/// Pins the join-side tolerance contract for <see cref="PhxJoinPayload.Platform"/>: an
/// unknown wire spelling on the <c>platform</c> member must round-trip to <c>null</c>
/// WITHOUT throwing a <see cref="JsonException"/>, and — critically — must leave every
/// sibling member (<c>token</c>, <c>protocolVersion</c>, <c>isReconnect</c>,
/// <c>forceBatch</c>) intact.
///
/// <para>
/// Regression guard for <c>Api_UserSocketEndpoint_AllowsWebSocketUpgrade</c>: the raw
/// dictionary payload in that integration test sends <c>platform="wasm"</c>, which
/// pre-fix threw a <see cref="JsonException"/> during
/// <c>Deserialize&lt;PhxJoinPayload&gt;</c>. The handler's outer try/catch then
/// substituted a defaulted <see cref="PhxJoinPayload"/> — wiping the token along with
/// the platform — and the join was answered with <c>Unauthorized</c>. Fast-tier
/// coverage here means a strict-converter regression on <c>Platform</c> breaks in
/// sub-second unit runs instead of surfacing as three-fixture flake in the
/// integration suite.
/// </para>
/// </summary>
public sealed class PhxJoinPayloadPlatformToleranceTests
{
    [Test]
    [Arguments("wasm")]        // the exact value from Api_UserSocketEndpoint_AllowsWebSocketUpgrade
    [Arguments("WASM")]        // case variant — EnumWire<T>.TryParse is case-insensitive, unknown all the same
    [Arguments("desktop")]     // unknown non-empty
    [Arguments("")]            // empty string — TryParse treats as unknown, must not throw
    [Arguments("   ")]         // whitespace — TryParse treats as unknown, must not throw
    public async Task Deserialize_WithUnknownPlatformString_ReturnsNullPlatform_AndPreservesSiblings(string platform)
    {
        // Mirrors the raw-dictionary shape the integration test uses: token + protocolVersion +
        // isReconnect + an unknown platform value. If the strict JsonStringEnumConverter is ever
        // re-attached to Platform, this deserialize call will throw and the assertions never run.
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
        // The tolerant converter must NOT weaken the happy path — every known wire spelling
        // (case-insensitive, per EnumWire<T>.TryParse) still parses to its enum member.
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
        // Platform is optional on the wire (JsonIgnore WhenWritingNull on the write side);
        // its absence must remain null rather than defaulting to a member — the join
        // branch treats null Platform as "not iOS" and skips the batched-init path
        // accordingly.
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
        // Non-string tokens on the platform member (e.g. a legacy client that once emitted a
        // numeric platform code) must degrade to null rather than propagate a JsonException
        // upstream — same tolerance shape as the unknown-string case.
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
        // Tolerant read must not skew the write side — Serialize<PhxJoinPayload>
        // still needs to emit the canonical wire vocabulary that the enum's
        // JsonStringEnumMemberNameAttribute pins.
        var payload = new PhxJoinPayload { Token = new SocketToken("t"), Platform = value };

        var json = JsonSerializer.Serialize(payload, SocketJson.Options);

        await Assert.That(json).Contains($"\"platform\":\"{expectedWire}\"")
            .Because($"Expected known platform {value} to serialize as the canonical wire spelling '{expectedWire}'.");
    }
}
