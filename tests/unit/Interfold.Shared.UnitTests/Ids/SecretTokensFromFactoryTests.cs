using Interfold.Shared.Contracts.Ids;

namespace Interfold.Api.UnitTests.Ids;

// Pins Jti.From, LinkToken.From, and Jti.NewJti. The From pair folds "null-or-blank
// → null, otherwise wrap" into a total function so raw credentials only exist inside
// the factory frame. NewJti is the same story on the write side.
public sealed class SecretTokensFromFactoryTests
{
    [Test]
    [Arguments((string?)null)]
    [Arguments("")]
    [Arguments(" ")]
    [Arguments("\t")]
    [Arguments("   ")]
    public async Task JtiFrom_NullOrBlankInput_ReturnsNull(string? input)
    {
        var result = Jti.From(input);

        await Assert.That(result).IsNull()
            .Because($"'{input ?? "<null>"}' must not produce a wrapped Jti — the AuthController null-check depends on From returning null for missing / blank JWT claims so the BadRequest 'Token is missing JTI claim' path runs.");
    }

    [Test]
    public async Task JtiFrom_ValidInput_ReturnsWrappedValue()
    {
        const string valid = "1a2b3c4d-5e6f-7890-abcd-ef0123456789";

        var result = Jti.From(valid);

        using (Assert.Multiple())
        {
            await Assert.That(result).IsNotNull()
                .Because("A non-blank JTI is a valid input and must wrap.");
            await Assert.That(result!.Value.Value).IsEqualTo(valid)
                .Because("From must not transform the input — the JTI is used verbatim as the revocation store key and any normalization would silently break logout invalidation across sessions.");
        }
    }

    [Test]
    [Arguments((string?)null)]
    [Arguments("")]
    [Arguments(" ")]
    [Arguments("\t")]
    [Arguments("   ")]
    public async Task LinkTokenFrom_NullOrBlankInput_ReturnsNull(string? input)
    {
        var result = LinkToken.From(input);

        await Assert.That(result).IsNull()
            .Because($"'{input ?? "<null>"}' must not produce a wrapped LinkToken — the AuthLinkController null-check depends on From returning null for the missing-query-AND-missing-cookie case so the 403 'invalid or expired' path runs instead of a phantom lookup.");
    }

    [Test]
    public async Task LinkTokenFrom_ValidInput_ReturnsWrappedValue()
    {
        const string valid = "97e6d3ab-cbfe-4a30-8f7e-6b2c8a3d9e1f";

        var result = LinkToken.From(valid);

        using (Assert.Multiple())
        {
            await Assert.That(result).IsNotNull()
                .Because("A non-blank link token is a valid input and must wrap.");
            await Assert.That(result!.Value.Value).IsEqualTo(valid)
                .Because("From must not transform the input — the link token is used verbatim as the Scylla lookup key and any normalization would silently break the one-time-link handshake.");
        }
    }

    // Belt-and-braces: incidental $"{jti}" between From and RevokeTokenAsync must go
    // through the redacting ToString.
    [Test]
    public async Task JtiFrom_WrappedValueRoundTripsThroughRedactedToString()
    {
        const string valid = "eyJhbGciOiJFUzI1NiIsInR5cCI6IkpXVCJ9.payload.signature";

        var result = Jti.From(valid);
        var interpolated = $"jti = {result?.ToString()}";

        using (Assert.Multiple())
        {
            await Assert.That(interpolated).DoesNotContain(valid)
                .Because("The wrapped Jti must redact under interpolation — the whole point of routing through the From factory is to ensure downstream string uses of the wrapped credential are safe by default.");
            await Assert.That(interpolated).StartsWith("jti = eyJh")
                .Because("Redaction is the first-4-chars + ellipsis form defined by SecretRedaction.Redact — pins the exact contract so a future 'safer' variant that returns '<redacted>' does not silently break log-parsing tools that grep for the prefix.");
        }
    }

    [Test]
    public async Task LinkTokenFrom_WrappedValueRoundTripsThroughRedactedToString()
    {
        const string valid = "0123456789abcdef0123456789abcdef";

        var result = LinkToken.From(valid);
        var interpolated = $"link_token = {result?.ToString()}";

        using (Assert.Multiple())
        {
            await Assert.That(interpolated).DoesNotContain(valid)
                .Because("The wrapped LinkToken must redact under interpolation — the AuthLinkController's cookie / redirect logs live in the same span as the From call and must not leak the token verbatim.");
            await Assert.That(interpolated).StartsWith("link_token = 0123")
                .Because("Pins the first-4-chars + ellipsis contract for LinkToken symmetrically with the Jti case.");
        }
    }

    [Test]
    public async Task JtiNewJti_ProducesUniqueValueEachCall()
    {
        var a = Jti.NewJti();
        var b = Jti.NewJti();

        await Assert.That(a.Value).IsNotEqualTo(b.Value)
            .Because("Two NewJti() calls must produce distinct values — a collision would silently merge the revocation entries for two independently-issued tokens, so revoking one would revoke both without any warning.");
    }

    // Wire format is Guid.NewGuid().ToString("N") — any drift orphans the revocation store.
    [Test]
    public async Task JtiNewJti_ProducesThirtyTwoCharLowercaseHex()
    {
        var jti = Jti.NewJti();

        using (Assert.Multiple())
        {
            await Assert.That(jti.Value.Length).IsEqualTo(32)
                .Because("Guid.NewGuid().ToString(\"N\") yields exactly 32 characters — a change here would break byte-identity with every issued JTI and orphan the revocation store on the first issue after deploy.");
            await Assert.That(jti.Value).Matches(@"^[0-9a-f]{32}$")
                .Because("Only lowercase hex; no dashes, no uppercase. Any drift would break byte-identity with the historical shape.");
        }
    }

    [Test]
    public async Task JtiNewJti_ValueRedactsUnderInterpolation()
    {
        var jti = Jti.NewJti();
        var interpolated = $"jti = {jti}";

        using (Assert.Multiple())
        {
            await Assert.That(interpolated).DoesNotContain(jti.Value)
                .Because("The freshly-minted Jti must redact under interpolation — the AuthController mint site logs several operation-id + redirect messages between NewJti and the RecordTokenAsync call, none of which should ever leak the JTI verbatim.");
            await Assert.That(interpolated).StartsWith($"jti = {jti.Value[..4]}")
                .Because("Redaction preserves the first 4 chars (per SecretRedaction.Redact) so operators can still correlate log lines across the mint / verify / revoke lifecycle without the full JTI being exposed.");
        }
    }
}
