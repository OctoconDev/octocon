using System.Text.Json;
using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;

namespace Interfold.Api.UnitTests.Ids;

/// <summary>
/// Pins the invariants that make <see cref="ScopedSystemId"/> a safe stand-in for hand-
/// formatted <c>$"{region}:{id}"</c> concatenations: idempotency across scoped/raw inputs
/// (so a repeated Compose call cannot double-prefix), byte-exact JSON wire compatibility
/// with <c>SystemId</c>, and strict parsing at trust boundaries. Pure C# with no host /
/// DI / IO; wire byte-freeze tests that need the full stack live in the integration suite.
/// </summary>
public sealed class ScopedSystemIdTests
{
    // ---------------- Compose: happy path + idempotency ---------------------

    /// <summary>
    /// Composing from a bare raw id produces the canonical <c>{region}:{rawId}</c> form.
    /// This is the "no-prefix" input shape (repositories generating a new id and asking
    /// the resolver to attach a region).
    /// </summary>
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

    /// <summary>
    /// Composing with an already-scoped input strips the existing prefix and re-applies
    /// the argument's region. Load-bearing anti-double-prefix invariant — without it a
    /// caller that passes an already-prefixed SystemId ends up with
    /// <c>"nam:nam:abcdefg"</c>.
    /// </summary>
    [Test]
    public async Task Compose_AlreadyScopedInput_IsIdempotent()
    {
        var fromRaw = ScopedSystemId.Compose(ScyllaKeyspace.Nam, "abcdefg");
        var fromScoped = ScopedSystemId.Compose(ScyllaKeyspace.Nam, "nam:abcdefg");

        await Assert.That(fromScoped.Value).IsEqualTo(fromRaw.Value)
            .Because("Compose is the anti-double-prefix guardrail — a scoped input must produce the same Value as the equivalent raw input.");
    }

    /// <summary>
    /// Composing with a different-region prefix strips and re-applies to the argument region
    /// — this is deliberate. The friendship-canonicalisation code re-applies the *principal's*
    /// region to a candidate id that may have carried its own home-region prefix, and every
    /// persistence adapter also uses "explicit region wins" semantics.
    /// </summary>
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

    /// <summary>
    /// Compose from a <see cref="SystemId"/> is idempotent for the same reason the string
    /// overload is — the SystemId's Value may already carry a scoped form (this is the
    /// current live shape of every principal id).
    /// </summary>
    [Test]
    public async Task Compose_FromSystemId_MatchesStringOverload()
    {
        var scopedFromString = ScopedSystemId.Compose(ScyllaKeyspace.Sam, "sam:xyz1234");
        var scopedFromSystemId = ScopedSystemId.Compose(ScyllaKeyspace.Sam, new("sam:xyz1234"));

        await Assert.That(scopedFromSystemId.Value).IsEqualTo(scopedFromString.Value)
            .Because("The SystemId overload is a strict alias for the string overload — same idempotency, same output.");
    }

    /// <summary>
    /// An unrecognised prefix (e.g. <c>"username:"</c>, <c>"discord:"</c>) is NOT a region and
    /// must NOT be stripped. This preserves the <c>ScyllaUserRegistryRegionContext.LookupAsync</c>
    /// branch that routes those prefixes to the discriminator column.
    /// </summary>
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
        // "nam:" strips the prefix, leaving "" (blank) — must fail with the same error as a
        // bare blank input rather than emitting "nam:" and getting stored as a phantom row.
        await Assert.That(() => ScopedSystemId.Compose(ScyllaKeyspace.Nam, "nam:"))
            .Throws<ArgumentException>()
            .Because("A bare region prefix with no raw id is a caller bug — never produce a scoped id whose raw component is empty.");
    }

    // ---------------- TryParseScoped: strict at trust boundaries ------------

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

    /// <summary>
    /// Case-normalisation on parse: the canonical wire form is lowercase (matches the
    /// <see cref="ScyllaKeyspace"/> wire values). Preserves the six existing comparison
    /// sites' assumption that Value is comparable byte-for-byte with a canonical form.
    /// </summary>
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
    [Arguments("abcdefg")]           // unscoped bare id
    [Arguments(":abcdefg")]          // empty region
    [Arguments("nam:")]              // empty raw
    [Arguments("xxx:abcdefg")]       // unknown region tag
    [Arguments("username:alice")]    // non-region discriminator prefix
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

    // ---------------- IParsable (ASP.NET route binding) ---------------------

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
        // Route-bound values come from client URLs; unscoped input is deliberately a 400
        // rather than a silent success (matches ParseScoped's strictness).
        await Assert.That(() => ScopedSystemId.Parse("no-scope", provider: null))
            .Throws<ArgumentException>();
    }

    // ---------------- Wire compatibility with SystemId ----------------------

    /// <summary>
    /// The JSON representation MUST be byte-identical to <see cref="SystemId"/>'s. A
    /// serialised <see cref="ScopedSystemId"/> and a serialised <see cref="SystemId"/> holding
    /// the same wire string produce identical bytes — this is the pre-condition for
    /// retyping <c>CommandEnvelope.PrincipalId</c> and <c>ITargetedClusterEvent.TargetSystemId</c>
    /// without a wire migration.
    /// </summary>
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
        // Strict JSON deserialise — a bad publisher that hand-crafts an unscoped id gets
        // caught at the byte boundary, not silently stored as an invalid ScopedSystemId.
        await Assert.That(() => JsonSerializer.Deserialize<ScopedSystemId>("\"unscoped\""))
            .Throws<ArgumentException>();
    }

    // ---------------- Value equality (record struct semantics) --------------

    /// <summary>
    /// Two <see cref="ScopedSystemId"/>s with the same <see cref="ScopedSystemId.Value"/> are
    /// equal — the record-struct default. This is the semantics
    /// <c>InProcessEventBus</c>'s <c>TargetSystemId == subscriber.SystemId</c> filter relies on.
    /// </summary>
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

    // ---------------- WireEnum delegation parity ----------------------------
    // TryParseRegion (called by TryParseScoped for the prefix side) now delegates
    // to EnumWire<ScyllaKeyspace>.TryParse. These tests pin that behaviour so a
    // future refactor cannot silently reintroduce a hand-rolled switch that drifts
    // from the JsonStringEnumMemberName attributes on the enum.

    /// <summary>
    /// Every wire spelling produced by EnumWire's ToWire (i.e. every declared enum
    /// member) must parse back through the scoped path. A regression here means the
    /// hand-rolled switch has come back and skipped a region.
    /// </summary>
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

    /// <summary>
    /// Uppercase / mixed-case region prefixes are accepted (EnumWire's TryParse is
    /// case-insensitive) and normalise to the lowercase wire form on the way out.
    /// </summary>
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

    /// <summary>
    /// Every unknown region tag must be rejected. This pins the "no silent default"
    /// contract at the scoped-parse boundary — TryParseRegion drops the
    /// blank-defaults-to-Nam fallback that <c>EnumWireExtensions.ParseScyllaKeyspace</c>
    /// carries.
    /// </summary>
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
