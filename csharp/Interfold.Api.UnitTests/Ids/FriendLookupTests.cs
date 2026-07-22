using Interfold.Contracts.Ids;

namespace Interfold.Api.UnitTests.Ids;

// Routing table FriendLookup.TryParse exposes at the friend-request wire boundary.
// Two-kind (Id / Username) universe; region-scoped ids and Discord snowflakes fail
// TryParse and surface as 400 at ASP.NET route binding.
public sealed class FriendLookupTests
{
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
    [Arguments(":abcdefg")]
    [Arguments("username:")]
    [Arguments("id:")]
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

    // Region-scoped and Discord shapes fail FriendLookup.TryParse — surfaced as 400 at
    // ASP.NET route binding. Region resolution still accepts them via UserRegistryLookup.
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

    [Test]
    public async Task TryParse_MultipleColons_TreatsFirstColonAsPrefixSeparator()
    {
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

    // Parse throws for the same inputs TryParse rejects — used by the JSON converter.
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

    // Implicit widen to string returns OriginalValue so historical string.IsNullOrWhiteSpace /
    // interpolation callers keep their raw-wire shape after the type flip.
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
