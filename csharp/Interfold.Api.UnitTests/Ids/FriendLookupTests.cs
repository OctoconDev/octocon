using Interfold.Contracts.Ids;

namespace Interfold.Api.UnitTests.Ids;

/// <summary>
/// Pins the routing table <see cref="FriendLookup.TryParse"/> exposes at the friend-request
/// wire boundary. The two-kind universe (Id / Username) replaced the pre-merge four-kind
/// <c>LookupHandle</c>; region-scoped ids and Discord snowflakes now fail
/// <see cref="FriendLookup.TryParse"/> and surface as a 400 at ASP.NET route binding rather
/// than falling through to a repository lane.
///
/// <para>
/// The unit is pure C# — no host, no IO — so this is the fast tier. The IO-bound
/// consequences of these routings are pinned by <c>ResolveUserIdDispatchTests</c>
/// (in-process fakes) and the integration <c>SendFriendRequestPrefixTests</c> (real backends,
/// including the "unparseable route segment → 400" pin from ASP.NET's IParsable pipeline).
/// </para>
/// </summary>
public sealed class FriendLookupTests
{
    // ---------------- Discriminator prefixes --------------------------------

    [Test]
    public async Task TryParse_UsernamePrefix_KindUsername()
    {
        var parsed = FriendLookup.TryParse("username:alice", provider: null, out var handle);

        using (Assert.Multiple())
        {
            await Assert.That(parsed).IsTrue();
            await Assert.That(handle.Kind).IsEqualTo(FriendLookupKind.Username);
            await Assert.That(handle.Value).IsEqualTo("alice")
                .Because("Value exposes the after-colon content — the 'username:' prefix is metadata, not part of the lookup value.");
            await Assert.That(handle.OriginalValue).IsEqualTo("username:alice")
                .Because("OriginalValue is retained verbatim so JSON serialization and idempotency hashes stay byte-identical to the pre-merge wire form.");
        }
    }

    [Test]
    public async Task TryParse_IdPrefix_KindId_StripsPrefix()
    {
        var parsed = FriendLookup.TryParse("id:abcdefg", provider: null, out var handle);

        using (Assert.Multiple())
        {
            await Assert.That(parsed).IsTrue();
            await Assert.That(handle.Kind).IsEqualTo(FriendLookupKind.Id)
                .Because("Explicit id: prefix is the strict assertion that the value is a system id — same registry column as bare-id but the caller made the intent explicit.");
            await Assert.That(handle.Value).IsEqualTo("abcdefg");
            await Assert.That(handle.OriginalValue).IsEqualTo("id:abcdefg");
        }
    }

    // ---------------- Bare id (no colon) ------------------------------------

    [Test]
    public async Task TryParse_BareId_KindId_ValueIsWholeInput()
    {
        var parsed = FriendLookup.TryParse("abcdefg", provider: null, out var handle);

        using (Assert.Multiple())
        {
            await Assert.That(parsed).IsTrue();
            await Assert.That(handle.Kind).IsEqualTo(FriendLookupKind.Id);
            await Assert.That(handle.Value).IsEqualTo("abcdefg")
                .Because("A bare handle has no prefix to strip — Value is the whole input.");
            await Assert.That(handle.OriginalValue).IsEqualTo("abcdefg")
                .Because("OriginalValue equals Value for bare shapes; the raw wire and after-prefix content are the same string.");
        }
    }

    // ---------------- Case-insensitivity on discriminator prefixes ----------

    [Test]
    [Arguments("USERNAME:alice", FriendLookupKind.Username)]
    [Arguments("ID:abcdefg", FriendLookupKind.Id)]
    [Arguments("Id:abcdefg", FriendLookupKind.Id)]
    public async Task TryParse_DiscriminatorPrefix_CaseInsensitive(string input, FriendLookupKind expected)
    {
        var parsed = FriendLookup.TryParse(input, provider: null, out var handle);

        using (Assert.Multiple())
        {
            await Assert.That(parsed).IsTrue()
                .Because("Discriminator prefixes are normalised case-insensitively — a client that shouts USERNAME must dispatch identically to the lowercase form.");
            await Assert.That(handle.Kind).IsEqualTo(expected);
        }
    }

    // ---------------- Strict rejection --------------------------------------

    [Test]
    [Arguments((string?)null)]
    [Arguments("")]
    [Arguments("   ")]
    public async Task TryParse_NullOrBlank_ReturnsFalse(string? input)
    {
        var parsed = FriendLookup.TryParse(input, provider: null, out var handle);

        using (Assert.Multiple())
        {
            await Assert.That(parsed).IsFalse();
            await Assert.That(handle).IsEqualTo(default(FriendLookup));
        }
    }

    [Test]
    [Arguments(":abcdefg")]       // empty prefix
    [Arguments("username:")]      // bare discriminator prefix, no raw
    [Arguments("id:")]            // bare discriminator prefix, no raw
    public async Task TryParse_BarePrefixOrEmptyHalf_ReturnsFalse(string input)
    {
        var parsed = FriendLookup.TryParse(input, provider: null, out var handle);

        using (Assert.Multiple())
        {
            await Assert.That(parsed).IsFalse()
                .Because($"'{input}' has one half empty — never dispatch a partially-formed handle to a registry column.");
            await Assert.That(handle).IsEqualTo(default(FriendLookup));
        }
    }

    [Test]
    [Arguments("xxx:abcdefg")]
    [Arguments("phone:0123456789")]
    [Arguments("bogus:whatever")]
    public async Task TryParse_UnknownPrefix_ReturnsFalse(string input)
    {
        var parsed = FriendLookup.TryParse(input, provider: null, out var handle);

        using (Assert.Multiple())
        {
            await Assert.That(parsed).IsFalse()
                .Because($"'{input}' has an unknown prefix that is not on the id/username allow-list — the friend-request route must reject rather than silently strip.");
            await Assert.That(handle).IsEqualTo(default(FriendLookup));
        }
    }

    /// <summary>
    /// The pre-merge <c>LookupHandle</c> parsed these into <c>Kind.Region</c> /
    /// <c>Kind.Discord</c>. Under the tightened friend-request contract they are exactly
    /// the shapes ASP.NET's IParsable route binding surfaces as a 400 — this pins the
    /// unit-level side of the "400 at route binding" contract that
    /// <c>SendFriendRequestPrefixTests</c> exercises end-to-end. Region resolution still
    /// accepts these shapes internally via the Scylla-owned <c>UserRegistryLookup</c>;
    /// the friend-request lookup does not.
    /// </summary>
    [Test]
    [Arguments("nam:abcdefg")]
    [Arguments("eur:abcdefg")]
    [Arguments("sam:abcdefg")]
    [Arguments("sas:abcdefg")]
    [Arguments("eas:abcdefg")]
    [Arguments("ocn:abcdefg")]
    [Arguments("gdpr:abcdefg")]
    [Arguments("discord:1234567890")]
    public async Task TryParse_RegionOrDiscordShape_ReturnsFalse(string input)
    {
        var parsed = FriendLookup.TryParse(input, provider: null, out var handle);

        using (Assert.Multiple())
        {
            await Assert.That(parsed).IsFalse()
                .Because($"'{input}' was accepted by the pre-merge four-kind LookupHandle but is out of scope for the friend-request route — it must surface as a 400 at ASP.NET route binding rather than falling through to a repository lane.");
            await Assert.That(handle).IsEqualTo(default(FriendLookup));
        }
    }

    // ---------------- Multi-colon in username -------------------------------

    [Test]
    public async Task TryParse_MultipleColons_TreatsFirstColonAsPrefixSeparator()
    {
        // "username:foo:bar" → prefix "username", value "foo:bar". The value can itself
        // contain colons (e.g. a URI-like handle). The parser only ever splits on the
        // first colon; anything after belongs to Value.
        var parsed = FriendLookup.TryParse("username:foo:bar", provider: null, out var handle);

        using (Assert.Multiple())
        {
            await Assert.That(parsed).IsTrue();
            await Assert.That(handle.Kind).IsEqualTo(FriendLookupKind.Username);
            await Assert.That(handle.Value).IsEqualTo("foo:bar")
                .Because("The parser splits on the first colon only so a username value can carry embedded colons (URI-like handles).");
            await Assert.That(handle.OriginalValue).IsEqualTo("username:foo:bar");
        }
    }

    // ---------------- Parse strict-throws contract --------------------------

    /// <summary>
    /// <see cref="FriendLookup.Parse"/> throws for the same inputs that <see cref="FriendLookup.TryParse"/>
    /// rejects — the strict path is used by the JSON converter and any imperative
    /// consumer that doesn't want to layer its own "did it parse" check.
    /// </summary>
    [Test]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("nam:abcdefg")]
    [Arguments("discord:1234567890")]
    [Arguments("xxx:abcdefg")]
    [Arguments(":abcdefg")]
    [Arguments("id:")]
    public async Task Parse_InvalidInput_ThrowsFormatException(string input)
    {
        await Assert.That(() => FriendLookup.Parse(input, provider: null))
            .Throws<FormatException>()
            .Because($"Parse is the strict path — '{input}' must throw rather than default so persisted-payload JSON deserialization surfaces the shape mismatch as an exception rather than silently corrupting a hash.");
    }

    // ---------------- Implicit widen ----------------------------------------

    /// <summary>
    /// The implicit widen to <see cref="string"/> returns <see cref="FriendLookup.OriginalValue"/>,
    /// so historical <c>string.IsNullOrWhiteSpace(handle)</c> / interpolation callers keep
    /// their raw-wire shape after the type flip.
    /// </summary>
    [Test]
    [Arguments("abcdefg")]
    [Arguments("id:abcdefg")]
    [Arguments("username:alice")]
    public async Task ImplicitWiden_ReturnsOriginalValue(string input)
    {
        var handle = FriendLookup.Parse(input, provider: null);

        string widened = handle;

        await Assert.That(widened).IsEqualTo(input)
            .Because("Implicit widen returns OriginalValue verbatim — anything else would break historical callers that rely on the raw-wire byte-for-byte shape.");
    }
}
