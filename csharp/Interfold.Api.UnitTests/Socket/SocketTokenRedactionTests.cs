using Interfold.Contracts.Ids;

namespace Interfold.Api.UnitTests.Socket;

/// <summary>
/// Pins the redaction contract that <see cref="SocketToken"/> provides — the safety
/// guarantee <c>WebSocketHandler.cs</c> depends on. The handler threads the JWT token
/// through six hops between query-string extraction and the final
/// <c>Authorization: Bearer</c> header write; the wrapper's <c>ToString()</c> returns
/// the redacted <c>"abcd…"</c> form so an <c>$"{token}"</c> interpolation is safe by
/// default, and the raw value is only reachable via an explicit <c>.Value</c> unwrap.
///
/// <para>
/// Fast-tier safety net: if a future edit weakens
/// <c>SocketToken.ToString()</c> (e.g. someone "helpfully" flips it to return
/// <c>Value</c> for easier debugging), these tests break in sub-second unit runs rather
/// than silently letting the socket handler start emitting JWTs into structured logs.
/// </para>
/// </summary>
public sealed class SocketTokenRedactionTests
{
    // A representative JWT-shaped value. The exact bytes don't matter — the redaction rule is
    // "first 4 chars + ellipsis for anything longer than 4 chars" — but using a plausible
    // opaque token here documents what a caller will actually pass in production.
    private const string SampleToken = "eyJhbGciOiJFUzI1NiIsInR5cCI6IkpXVCJ9.payload.signature";

    [Test]
    public async Task ToString_OnPopulatedToken_RedactsToFirstFourCharsPlusEllipsis()
    {
        // An accidental $"{token}" interpolation in any structured log message along the
        // six-hop socket path must not leak the JWT. If this changes to return Value
        // verbatim, every log statement in WebSocketHandler.cs that mentions the token
        // becomes a credential-leak vector.
        SocketToken token = new(SampleToken);

        await Assert.That(token.ToString()).IsEqualTo("eyJh…")
            .Because("SocketToken.ToString() must redact to the first-4-chars + ellipsis form so log-shaped uses of the wrapper cannot leak the JWT. WebSocketHandler.cs threads the wrapper through every hop specifically to lean on this contract.");
    }

    [Test]
    public async Task InterpolatedInString_UsesRedactedForm_NotRawValue()
    {
        // Concrete restatement of the log pattern the wrapper closes: any code path
        // that writes $"token = {token}" gets the redacted form, and the raw JWT never
        // appears in the resulting string. A regression on ToString() would show up
        // here as the interpolation switching back to the full token bytes.
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
        // The two survival points in WebSocketHandler.cs (framework JwtSecurityTokenHandler
        // calls and the Authorization: Bearer header write) explicitly reach for .Value. Pin
        // that this unwrap is truly identity — a future 'safer' Value that also redacted
        // would break every socket authentication silently.
        SocketToken token = new(SampleToken);

        await Assert.That(token.Value).IsEqualTo(SampleToken)
            .Because(".Value is the deliberate escape hatch — it must return the raw JWT unmodified so JwtSecurityTokenHandler can validate the signature and so the loopback endpoint proxy emits a valid Authorization: Bearer header.");
    }

    [Test]
    public async Task ToString_OnShortToken_CollapsesToSingleEllipsis()
    {
        // Edge case pin: SecretRedaction.Redact returns "…" for values of length ≤ 4 (no
        // safe prefix to expose). A production JWT will never be this short, but the socket
        // handler's guard is IsNullOrWhiteSpace, which does NOT catch a 1–4 char string. If
        // someone passes a stub token in a test or a malformed client sends one, the log
        // must still redact rather than emitting the whole thing.
        SocketToken token = new("abc");

        await Assert.That(token.ToString()).IsEqualTo("…")
            .Because("Sub-5-char values collapse to a lone ellipsis so the redaction never accidentally reveals more than 4 characters of any secret.");
    }

    [Test]
    public async Task RecordStructEquality_ComparesRawValuesOrdinally()
    {
        // WebSocketHandler.cs line 174 uses `payloadToken == token` to gate the phx_join
        // — this test pins that the record-struct-generated equality is in fact ordinal
        // string equality, so the join gate can never accidentally admit a case-variant or
        // otherwise-non-byte-identical token.
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
