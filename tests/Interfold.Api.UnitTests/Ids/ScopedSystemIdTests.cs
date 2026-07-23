using System.Text.Json;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;

namespace Interfold.Api.UnitTests.Ids;

// Pins the invariants that make ScopedSystemId a safe stand-in for hand-formatted
// $"{region}:{id}" concatenations: idempotency across scoped/raw inputs, byte-exact
// JSON wire compatibility with SystemId, and strict parsing at trust boundaries.
public sealed class ScopedSystemIdTests
{
    [Test]
    public async Task Compose_RawId_ProducesScopedValue()
    {
        var scoped = ScopedSystemId.Compose(ScyllaKeyspace.Nam, "abcdefg");

        using (Assert.Multiple())
        {
            await Assert.That(scoped.Value).IsEqualTo("nam:abcdefg")
                .Because("Compose from a bare id must emit the canonical scoped form so persistence adapters see a single wire shape.");
            await Assert.That(scoped.RawId).IsEqualTo("abcdefg")
                .Because("RawId exposes the bare id for consumers (Scylla PKs) that need the region-stripped shape.");
            await Assert.That(scoped.Region).IsEqualTo(ScyllaKeyspace.Nam)
                .Because("Region reflects the composed-with argument, not a lazy parse of Value.");
        }
    }

    // Load-bearing anti-double-prefix invariant: without idempotency an already-prefixed
    // input becomes "nam:nam:abcdefg".
    [Test]
    public async Task Compose_AlreadyScopedInput_IsIdempotent()
    {
        var fromRaw = ScopedSystemId.Compose(ScyllaKeyspace.Nam, "abcdefg");
        var fromScoped = ScopedSystemId.Compose(ScyllaKeyspace.Nam, "nam:abcdefg");

        await Assert.That(fromScoped.Value).IsEqualTo(fromRaw.Value)
            .Because("Compose is the anti-double-prefix guardrail — a scoped input must produce the same Value as the equivalent raw input.");
    }

    // Explicit-region-wins matches FriendshipIdNormalization.CanonicalizeForPrincipal
    // and every persistence adapter's semantics.
    [Test]
    public async Task Compose_DifferentRegionPrefix_ArgumentRegionWins()
    {
        var scoped = ScopedSystemId.Compose(ScyllaKeyspace.Eur, "nam:abcdefg");

        using (Assert.Multiple())
        {
            await Assert.That(scoped.Value).IsEqualTo("eur:abcdefg")
                .Because("Compose treats the region argument as authoritative — matches FriendshipIdNormalization.CanonicalizeForPrincipal.");
            await Assert.That(scoped.Region).IsEqualTo(ScyllaKeyspace.Eur);
        }
    }

    [Test]
    public async Task Compose_FromSystemId_MatchesStringOverload()
    {
        var scopedFromString = ScopedSystemId.Compose(ScyllaKeyspace.Sam, "sam:xyz1234");
        var scopedFromSystemId = ScopedSystemId.Compose(ScyllaKeyspace.Sam, new("sam:xyz1234"));

        await Assert.That(scopedFromSystemId.Value).IsEqualTo(scopedFromString.Value)
            .Because("The SystemId overload is a strict alias for the string overload — same idempotency, same output.");
    }

    // Non-region prefixes (username:, discord:) are opaque so ScyllaUserRegistryRegionContext's
    // discriminator-column routing keeps working.
    [Test]
    public async Task Compose_UnknownPrefixNotStripped()
    {
        var scoped = ScopedSystemId.Compose(ScyllaKeyspace.Nam, "username:alice");

        await Assert.That(scoped.Value).IsEqualTo("nam:username:alice")
            .Because("Non-region prefixes are opaque to Compose — stripping them would silently corrupt the discriminator-column routing.");
    }

    [Test]
    public async Task Compose_BlankId_Throws()
    {
        await Assert.That(() => ScopedSystemId.Compose(ScyllaKeyspace.Nam, "   "))
            .Throws<ArgumentException>()
            .Because("A blank id is never a valid input — surface the caller bug at construction, not on the first byte written to the wire.");
    }

    [Test]
    public async Task Compose_OnlyPrefixNoRaw_Throws()
    {
        await Assert.That(() => ScopedSystemId.Compose(ScyllaKeyspace.Nam, "nam:"))
            .Throws<ArgumentException>()
            .Because("A bare region prefix with no raw id is a caller bug — never produce a scoped id whose raw component is empty.");
    }

    [Test]
    public async Task TryParseScoped_ValidScoped_ReturnsTrue()
    {
        var parsed = ScopedSystemId.TryParseScoped("eur:acct-42", out var result);

        using (Assert.Multiple())
        {
            await Assert.That(parsed).IsTrue();
            await Assert.That(result.Value).IsEqualTo("eur:acct-42");
            await Assert.That(result.Region).IsEqualTo(ScyllaKeyspace.Eur);
            await Assert.That(result.RawId).IsEqualTo("acct-42");
        }
    }

    // Canonical wire form is lowercase (matches ScyllaKeyspace's JsonStringEnumMemberName).
    [Test]
    public async Task TryParseScoped_MixedCaseRegion_Normalises()
    {
        var parsed = ScopedSystemId.TryParseScoped("EUR:acct-42", out var result);

        using (Assert.Multiple())
        {
            await Assert.That(parsed).IsTrue();
            await Assert.That(result.Value).IsEqualTo("eur:acct-42")
                .Because("The wire form must be lowercase-canonical so downstream string comparisons in SystemTopic and ScyllaKeyspaceResolver don't spuriously miss.");
        }
    }

    [Test]
    [Arguments((string?)null)]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("abcdefg")]
    [Arguments(":abcdefg")]
    [Arguments("nam:")]
    [Arguments("xxx:abcdefg")]
    [Arguments("username:alice")]
    public async Task TryParseScoped_InvalidInputs_ReturnsFalse(string? input)
    {
        var parsed = ScopedSystemId.TryParseScoped(input, out var result);

        using (Assert.Multiple())
        {
            await Assert.That(parsed).IsFalse()
                .Because($"'{input ?? "<null>"}' is not a valid scoped system id and TryParse must never throw or silently succeed for it.");
            await Assert.That(result).IsEqualTo(default(ScopedSystemId));
        }
    }

    [Test]
    public async Task ParseScoped_InvalidInput_Throws()
    {
        await Assert.That(() => ScopedSystemId.ParseScoped("not-scoped"))
            .Throws<ArgumentException>()
            .Because("ParseScoped is the strict variant used at trust boundaries — unscoped input surfaces as a fail-fast rather than a silent default.");
    }

    [Test]
    public async Task IParsable_TryParse_DelegatesToTryParseScoped()
    {
        var parsed = ScopedSystemId.TryParse("nam:principal-a", provider: null, out var result);

        using (Assert.Multiple())
        {
            await Assert.That(parsed).IsTrue();
            await Assert.That(result.Value).IsEqualTo("nam:principal-a");
        }
    }

    [Test]
    public async Task IParsable_Parse_ThrowsOnUnscoped()
    {
        await Assert.That(() => ScopedSystemId.Parse("no-scope", provider: null))
            .Throws<ArgumentException>();
    }

    // Byte-identical JSON with SystemId is the precondition for retyping
    // CommandEnvelope.PrincipalId / ITargetedClusterEvent.TargetSystemId without migration.
    [Test]
    public async Task Json_RoundTripsThroughValue()
    {
        var scoped = ScopedSystemId.Compose(ScyllaKeyspace.Nam, "principal-a");

        var scopedJson = JsonSerializer.Serialize(scoped);
        var systemIdJson = JsonSerializer.Serialize(new SystemId("nam:principal-a"));

        using (Assert.Multiple())
        {
            await Assert.That(scopedJson).IsEqualTo("\"nam:principal-a\"")
                .Because("The scoped Value must be emitted verbatim — anything else changes the on-wire shape for consumers.");
            await Assert.That(scopedJson).IsEqualTo(systemIdJson)
                .Because("The migration story depends on ScopedSystemId serialising byte-identically to SystemId — otherwise retyping breaks wire compatibility.");

            var reserialised = JsonSerializer.Deserialize<ScopedSystemId>(scopedJson);
            await Assert.That(reserialised.Value).IsEqualTo(scoped.Value)
                .Because("Deserialise-then-serialise must be a fixed point for anything we route through Kafka or a snapshot store.");
        }
    }

    [Test]
    public async Task Json_DeserialiseUnscoped_Throws()
    {
        await Assert.That(() => JsonSerializer.Deserialize<ScopedSystemId>("\"unscoped\""))
            .Throws<ArgumentException>();
    }

    // Record-struct equality is what InProcessEventBus's TargetSystemId filter relies on.
    [Test]
    public async Task Equality_SameValue_Equal()
    {
        var a = ScopedSystemId.Compose(ScyllaKeyspace.Nam, "abcdefg");
        var b = ScopedSystemId.Compose(ScyllaKeyspace.Nam, "nam:abcdefg");

        using (Assert.Multiple())
        {
            await Assert.That(a).IsEqualTo(b);
            await Assert.That(a.GetHashCode()).IsEqualTo(b.GetHashCode());
        }
    }

    [Test]
    public async Task AsSystemId_PreservesValueByteExact()
    {
        var scoped = ScopedSystemId.Compose(ScyllaKeyspace.Eur, "acct-42");
        var systemId = scoped.AsSystemId();

        await Assert.That(systemId.Value).IsEqualTo(scoped.Value)
            .Because("AsSystemId is the wire-boundary escape hatch — it must produce the identical byte string, not a re-parse.");
    }

    // TryParseRegion delegates to EnumWire<ScyllaKeyspace>.TryParse; these tests catch a
    // hand-rolled switch drifting from the JsonStringEnumMemberName attributes on the enum.
    [Test]
    public async Task TryParseScoped_EveryWireRegion_Parses()
    {
        foreach (var region in Enum.GetValues<ScyllaKeyspace>())
        {
            var wire = EnumWire<ScyllaKeyspace>.ToWire(region);
            var parsed = ScopedSystemId.TryParseScoped($"{wire}:abcdefg", out var result);

            using (Assert.Multiple())
            {
                await Assert.That(parsed).IsTrue()
                    .Because($"'{wire}:abcdefg' must parse — the wire tag came directly from EnumWire.ToWire so any TryParse mismatch indicates a hand-rolled table drifted from the enum.");
                await Assert.That(result.Region).IsEqualTo(region);
            }
        }
    }

    [Test]
    [Arguments("NAM")]
    [Arguments("Eur")]
    [Arguments("gDpR")]
    public async Task TryParseScoped_CaseInsensitiveRegion_Normalises(string mixedCase)
    {
        var parsed = ScopedSystemId.TryParseScoped($"{mixedCase}:abcdefg", out var result);
        var expected = EnumWire<ScyllaKeyspace>.ToWire(
            Enum.Parse<ScyllaKeyspace>(char.ToUpperInvariant(mixedCase[0]) + mixedCase[1..].ToLowerInvariant()));

        using (Assert.Multiple())
        {
            await Assert.That(parsed).IsTrue();
            await Assert.That(result.Value).IsEqualTo($"{expected}:abcdefg")
                .Because("Wire values must be lowercase-canonical regardless of client casing — this is what six downstream comparison sites assume.");
        }
    }

    // No blank-defaults-to-Nam fallback: TryParseRegion drops the tolerant behaviour
    // that EnumWireExtensions.ParseScyllaKeyspace carries.
    [Test]
    [Arguments("xxx:abcdefg")]
    [Arguments("dev:abcdefg")]
    [Arguments("northamerica:abcdefg")]
    public async Task TryParseScoped_UnknownRegion_ReturnsFalse(string wire)
    {
        var parsed = ScopedSystemId.TryParseScoped(wire, out _);
        await Assert.That(parsed).IsFalse();
    }
}
