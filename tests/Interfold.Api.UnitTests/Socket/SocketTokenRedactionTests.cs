using Interfold.Shared.Contracts.Ids;

namespace Interfold.Api.UnitTests.Socket;

// Pins SocketToken's redaction contract. ToString() returns "abcd…" so an accidental
// $"{token}" in any WebSocketHandler log statement never leaks the JWT; raw bytes only
// reachable via explicit .Value.
public sealed class SocketTokenRedactionTests
{
    private const string SampleToken = "eyJhbGciOiJFUzI1NiIsInR5cCI6IkpXVCJ9.payload.signature";

    [Test]
    public async Task ToString_OnPopulatedToken_RedactsToFirstFourCharsPlusEllipsis()
    {
        SocketToken token = new(SampleToken);

        await Assert.That(token.ToString()).IsEqualTo("eyJh…")
            .Because("SocketToken.ToString() must redact to the first-4-chars + ellipsis form so log-shaped uses of the wrapper cannot leak the JWT. WebSocketHandler.cs threads the wrapper through every hop specifically to lean on this contract.");
    }

    [Test]
    public async Task InterpolatedInString_UsesRedactedForm_NotRawValue()
    {
        SocketToken token = new(SampleToken);

        var interpolated = $"socket token = {token}";

        await Assert.That(interpolated).IsEqualTo("socket token = eyJh…")
            .Because("String interpolation always calls ToString() — this asserts the redacted-by-default behaviour end-to-end so a caller can never accidentally leak the raw JWT via $\"{token}\".");
        await Assert.That(interpolated).DoesNotContain(SampleToken)
            .Because("Belt-and-braces: the raw token bytes must not appear anywhere in an interpolated string. Guards against a future ToString() that returns 'redacted + value' or similar 'helpful' variants.");
    }

    [Test]
    public async Task Value_ReturnsRawToken_Verbatim()
    {
        SocketToken token = new(SampleToken);

        await Assert.That(token.Value).IsEqualTo(SampleToken)
            .Because(".Value is the deliberate escape hatch — it must return the raw JWT unmodified so JwtSecurityTokenHandler can validate the signature and so the loopback endpoint proxy emits a valid Authorization: Bearer header.");
    }

    [Test]
    public async Task ToString_OnShortToken_CollapsesToSingleEllipsis()
    {
        // Values of length ≤ 4 have no safe prefix to expose; SecretRedaction returns "…".
        SocketToken token = new("abc");

        await Assert.That(token.ToString()).IsEqualTo("…")
            .Because("Sub-5-char values collapse to a lone ellipsis so the redaction never accidentally reveals more than 4 characters of any secret.");
    }

    [Test]
    public async Task RecordStructEquality_ComparesRawValuesOrdinally()
    {
        SocketToken a = new(SampleToken);
        SocketToken b = new(SampleToken);
        SocketToken different = new(SampleToken + "-tampered");
        SocketToken caseVariant = new(SampleToken.ToUpperInvariant());

        await Assert.That(a == b).IsTrue()
            .Because("Two SocketToken wrappers over the same raw string must compare equal — this is the equality WebSocketHandler.cs uses to gate `payload.token == query.token` on phx_join.");
        await Assert.That(a == different).IsFalse()
            .Because("Any byte difference in the raw value must produce inequality — the join gate must not admit a payload whose token differs from the one that authenticated the upgrade.");
        await Assert.That(a == caseVariant).IsFalse()
            .Because("JWTs are case-sensitive by construction; the record-struct equality must be ordinal (not ordinal-ignore-case) so an uppercased/lowercased copy is treated as a different credential.");
    }
}
