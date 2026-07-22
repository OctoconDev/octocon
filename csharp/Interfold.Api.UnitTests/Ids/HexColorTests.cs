using System.Text.Json;
using Interfold.Contracts.Ids;

namespace Interfold.Api.UnitTests.Ids;

// Pins the strict contract every HexColor construction path honours: ctor, Parse,
// TryParse, JSON reader, and FromNullable all reject non-well-formed input. Normalise
// (shared with the SP importer + HexColorFixupService) is exercised here too, since a
// bare six-char slip through would silently write malformed rows again.
public sealed class HexColorTests
{
    [Test]
    [Arguments("#F0A")]
    [Arguments("#FF00AA")]
    [Arguments("#ff00aa")]
    [Arguments("#FfAa00")]
    [Arguments("#FF00AAFF")]
    public async Task Constructor_WellFormed_Accepts(string input)
    {
        var color = new HexColor(input);

        using (Assert.Multiple())
        {
            await Assert.That(color.Value).IsEqualTo(input)
                .Because("The constructor preserves the input verbatim once validation passes; case and length are all callers' concern.");
            await Assert.That(color.IsWellFormed).IsTrue()
                .Because("A value that survived the constructor's validation must satisfy IsWellFormed — tautology for defence-in-depth checks.");
        }
    }

    [Test]
    [Arguments("")]
    [Arguments("red")]
    [Arguments("FF00AA")]
    [Arguments("#GGGGGG")]
    [Arguments("#FF00")]
    [Arguments("#FF00AAF")]
    public async Task Constructor_Malformed_Throws(string input)
    {
        await Assert.That(() => new HexColor(input)).ThrowsExactly<FormatException>()
            .Because("Every wire-side construction routes through the constructor; malformed input must fail-fast rather than persisting a bad value.");
    }

    [Test]
    public async Task Constructor_Null_Throws()
    {
        await Assert.That(() => new HexColor(null!)).ThrowsExactly<ArgumentNullException>()
            .Because("null is a caller bug, not malformed data — ArgumentNullException surfaces the parameter name.");
    }

    [Test]
    public async Task Parse_WellFormed_Roundtrips()
    {
        var color = HexColor.Parse("#FF00AA", provider: null);
        await Assert.That(color.Value).IsEqualTo("#FF00AA");
    }

    [Test]
    public async Task Parse_Malformed_Throws()
    {
        await Assert.That(() => HexColor.Parse("red", provider: null)).ThrowsExactly<FormatException>();
    }

    [Test]
    public async Task TryParse_WellFormed_ReturnsTrue()
    {
        var parsed = HexColor.TryParse("#FF00AA", provider: null, out var color);

        using (Assert.Multiple())
        {
            await Assert.That(parsed).IsTrue();
            await Assert.That(color.Value).IsEqualTo("#FF00AA");
        }
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("red")]
    [Arguments("FF00AA")]
    public async Task TryParse_MalformedOrNull_ReturnsFalse(string? input)
    {
        var parsed = HexColor.TryParse(input, provider: null, out _);

        await Assert.That(parsed).IsFalse()
            .Because("TryParse must never throw and must return false for every shape that would fail Parse — matches int.TryParse's contract.");
    }

    [Test]
    public async Task FromNullable_Null_ReturnsNull()
    {
        var color = HexColor.FromNullable(null);
        await Assert.That(color).IsNull()
            .Because("null input is 'no color stored' and must round-trip as null without validation.");
    }

    [Test]
    public async Task FromNullable_WellFormed_ReturnsHexColor()
    {
        var color = HexColor.FromNullable("#FF00AA");

        using (Assert.Multiple())
        {
            await Assert.That(color).IsNotNull();
            await Assert.That(color!.Value.Value).IsEqualTo("#FF00AA");
        }
    }

    [Test]
    [Arguments("")]
    [Arguments("red")]
    [Arguments("FF00AA")]
    [Arguments("#GG0000")]
    public async Task FromNullable_Malformed_Throws(string input)
    {
        await Assert.That(() => HexColor.FromNullable(input)).ThrowsExactly<FormatException>()
            .Because("The strict contract makes FromNullable throw on malformed input; the HexColorFixupService is the pre-flight that guarantees this never fires on well-migrated data.");
    }

    [Test]
    public async Task Json_WellFormed_Roundtrips()
    {
        var original = new HexColor("#FF00AA");
        var json = JsonSerializer.Serialize(original);

        using (Assert.Multiple())
        {
            await Assert.That(json).IsEqualTo("\"#FF00AA\"")
                .Because("The JSON emission is byte-identical to the raw string — persistence-adapter reads and idempotency-hashed replays depend on this.");

            var deserialised = JsonSerializer.Deserialize<HexColor>(json);
            await Assert.That(deserialised.Value).IsEqualTo(original.Value);
        }
    }

    [Test]
    [Arguments("\"\"")]
    [Arguments("\"red\"")]
    [Arguments("\"FF00AA\"")]
    [Arguments("\"#GG0000\"")]
    public async Task Json_Malformed_ThrowsJsonException(string malformedJson)
    {
        await Assert.That(() => JsonSerializer.Deserialize<HexColor>(malformedJson))
            .ThrowsExactly<JsonException>()
            .Because("Malformed JSON must surface as JsonException so ASP.NET Core model binding maps to a 400, matching the StringBackedIds house style.");
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    public async Task Normalise_NullOrBlank_ReturnsNull(string? input)
    {
        var result = HexColor.Normalise(input);
        await Assert.That(result).IsNull();
    }

    [Test]
    [Arguments("#FF00AA")]
    [Arguments("#F0A")]
    [Arguments("#FF00AAFF")]
    [Arguments("#ff00aa")]
    public async Task Normalise_AlreadyWellFormed_ReturnsInputVerbatim(string input)
    {
        var result = HexColor.Normalise(input);
        await Assert.That(result).IsEqualTo(input)
            .Because("An already-well-formed value must pass through unchanged so the fixup service treats it as a no-op (skip the UPDATE).");
    }

    [Test]
    [Arguments("FF00AA", "#FF00AA")]
    [Arguments("ff00aa", "#ff00aa")]
    [Arguments("  #FF00AA  ", "#FF00AA")]
    [Arguments("  FF00AA  ", "#FF00AA")]
    public async Task Normalise_RecoverableBareHex_ReturnsCanonicalForm(string input, string expected)
    {
        var result = HexColor.Normalise(input);
        await Assert.That(result).IsEqualTo(expected)
            .Because("Bare six-char hex is the SP importer's historical shape and the pre-strict client contract — Normalise prepends the # and trims whitespace so downstream construction succeeds.");
    }

    [Test]
    [Arguments("red")]
    [Arguments("FF00")]
    [Arguments("FF00AAFF")]
    [Arguments("#GGGGGG")]
    [Arguments("GGGGGG")]
    public async Task Normalise_Unrecoverable_ReturnsNull(string input)
    {
        var result = HexColor.Normalise(input);
        await Assert.That(result).IsNull()
            .Because("Anything not recoverable as exactly six hex digits (with optional #) is unrecoverable; the fixup service nulls the DB column in that case.");
    }
}
