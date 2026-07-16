using Interfold.Contracts.Ids;

namespace Interfold.Api.UnitTests.Ids;

/// <summary>
/// Pins the <c>Jti.From(string?)</c>, <c>LinkToken.From(string?)</c>, and
/// <c>Jti.NewJti()</c> factories. The <c>From</c> pair folds "null-or-blank → null,
/// otherwise wrap" into a single total function so the caller's null-check runs on the
/// typed nullable and the raw credential exists only inside the factory frame — closing
/// the window where a raw string local across the guard/wrap boundary could be leaked
/// by an incidental structured log. <c>NewJti</c> is the same story on the write side
/// for <c>AuthController.IssueDeepLinkTokenAsync</c>.
///
/// <para>
/// Deliberately small and targeted: the four-input matrix (null / empty /
/// whitespace-only / valid) for each <c>From</c> factory plus a three-test set for
/// <c>NewJti</c> (uniqueness, wire format, redaction). Broader wrapper-shape tests
/// belong with the wrapper itself.
/// </para>
/// </summary>
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
        // AuthController.RevokeToken calls Jti.From(User.FindFirst(JwtClaimNames.Jti)?.Value).
        // FindFirst returns null when the claim is absent; .Value is null when the claim
        // exists but carries no value; a whitespace-only claim is a spec violation that the
        // guard also rejects. All three collapse to the same "missing JTI" bad-request path,
        // so From must map all three to null. If a future edit accepts a blank string here,
        // RevokeTokenAsync would proceed to persist a whitespace-keyed revocation row.
        var result = Jti.From(input);

        await Assert.That(result).IsNull()
            .Because($"'{input ?? "<null>"}' must not produce a wrapped Jti — the AuthController null-check depends on From returning null for missing / blank JWT claims so the BadRequest 'Token is missing JTI claim' path runs.");
    }

    [Test]
    public async Task JtiFrom_ValidInput_ReturnsWrappedValue()
    {
        // Positive path: a real JTI (typically a Guid or opaque token) must round-trip
        // through From → .Value with the string preserved bit-for-bit. Any transformation
        // here (trim, normalize case, base64-decode) would corrupt the revocation lookup
        // key and silently break logout invalidation.
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
        // AuthLinkController.Callback calls LinkToken.From(await GetValueAsync(...) ?? cookie).
        // Both the query-string extraction and the cookie fallback can return null (missing)
        // or empty (present-but-empty). A whitespace-only value is a copy-paste-mangled URL
        // or a hostile client. All three collapse to the same 403 "invalid or expired" path,
        // so From must reject all three symmetrically. If From accepted blank, the resolver
        // would issue a Scylla lookup with a whitespace key and (with an unlucky test seed)
        // could match a corrupted row.
        var result = LinkToken.From(input);

        await Assert.That(result).IsNull()
            .Because($"'{input ?? "<null>"}' must not produce a wrapped LinkToken — the AuthLinkController null-check depends on From returning null for the missing-query-AND-missing-cookie case so the 403 'invalid or expired' path runs instead of a phantom lookup.");
    }

    [Test]
    public async Task LinkTokenFrom_ValidInput_ReturnsWrappedValue()
    {
        // Positive path: a real link token (Guid-shaped, minted by GET /settings/link_token)
        // must round-trip through From → .Value verbatim so the Scylla / InMemory account
        // repository's ResolveSystemIdByLinkTokenAsync uses the same key that was written
        // by the mint endpoint. Any From-side transformation would break the one-time-link
        // handshake with the exact "invalid or expired" symptom this factory closes.
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

    [Test]
    public async Task JtiFrom_WrappedValueRoundTripsThroughRedactedToString()
    {
        // Belt-and-braces on the safety story: after From returns a wrapped Jti, any
        // incidental $"{jti}" interpolation between the From call and RevokeTokenAsync
        // must go through the redacting ToString(). A regression that exposes plaintext
        // in ToString reopens the leak window this factory closes.
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
        // Symmetric belt-and-braces for LinkToken. The AuthLinkController Callback logs
        // several redirect / cookie state messages between the LinkToken.From call and the
        // ResolveSystemIdByLinkTokenAsync call; any of those can (now or in future) end up
        // interpolating the wrapped local. That must never leak the one-time link token
        // into a log line — the whole "wrap via From" idiom leans on this contract.
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

    // ---------------- Jti.NewJti() mint factory --------------------

    [Test]
    public async Task JtiNewJti_ProducesUniqueValueEachCall()
    {
        // AuthController.IssueDeepLinkTokenAsync calls NewJti() once per issued token; two
        // consecutive calls collided would mean two issued deep-link tokens share the same
        // revocation-store row key, so revoking one silently revokes both. The mint uses
        // Guid.NewGuid() under the hood which is a version-4 (random) GUID — collision
        // probability is astronomically low but must be pinned so a future edit that swaps
        // to a Guid.Empty stub for testing does not silently ship past this file.
        var a = Jti.NewJti();
        var b = Jti.NewJti();

        await Assert.That(a.Value).IsNotEqualTo(b.Value)
            .Because("Two NewJti() calls must produce distinct values — a collision would silently merge the revocation entries for two independently-issued tokens, so revoking one would revoke both without any warning.");
    }

    [Test]
    public async Task JtiNewJti_ProducesThirtyTwoCharLowercaseHex()
    {
        // Pins the wire format: 32 lowercase hex characters, no dashes. Matches the
        // Guid.NewGuid().ToString("N") shape byte-for-byte so every issued JWT verifies
        // against the same revocation-store row key. Any drift (uppercase, hyphens,
        // base64, different length) would orphan the revocation store on first deploy.
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
        // Belt-and-braces: after NewJti returns a wrapped Jti, any incidental $"{jti}"
        // in a log statement (or an exception message that captures the local) must go
        // through the redacting ToString(). Without this, the AuthController's
        // mint-and-record window could leak the freshly-minted JTI into structured logs
        // before the token is even returned to the OAuth callback client.
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
