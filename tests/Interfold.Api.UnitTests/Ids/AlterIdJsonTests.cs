using System.Text.Json;
using Interfold.Contracts.Ids;

namespace Interfold.Api.UnitTests.Ids;

// AlterId JSON contract with a short-backed value: bare JSON number, byte-identical
// round-trips in smallint range, out-of-range surfaces as JsonException at the byte
// boundary (400 before the controller runs, not just at ValidAlterIdAttribute).
public sealed class AlterIdJsonTests
{
    [Test]
    [Arguments((short)1)]
    [Arguments((short)1000)]
    [Arguments(short.MaxValue)]
    [Arguments((short)0)]
    [Arguments((short)-1)]
    [Arguments(short.MinValue)]
    public async Task Json_InRangeSmallint_RoundTrips(short value)
    {
        var original = new AlterId(value);
        var json = JsonSerializer.Serialize(original);
        var deserialised = JsonSerializer.Deserialize<AlterId>(json);

        using (Assert.Multiple())
        {
            await Assert.That(json).IsEqualTo(value.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Because("Wire emission is a bare JSON number matching short.ToString — byte-identical to the pre-narrowing shape, which is a precondition for idempotent replay of stored commands.");
            await Assert.That(deserialised.Value).IsEqualTo(value);
        }
    }

    [Test]
    [Arguments("32768")]
    [Arguments("-32769")]
    [Arguments("2147483647")]
    [Arguments("-2147483648")]
    [Arguments("9999999999999")]
    public async Task Json_OutOfShortRange_ThrowsJsonException(string numberLiteral)
    {
        await Assert.That(() => JsonSerializer.Deserialize<AlterId>(numberLiteral))
            .ThrowsExactly<JsonException>()
            .Because("The JSON reader must reject anything outside short range at the byte boundary — ASP.NET Core maps this to a 400 before the model-binding validator ever runs.");
    }

    [Test]
    [Arguments("\"5\"")]
    [Arguments("null")]
    [Arguments("true")]
    [Arguments("[1]")]
    public async Task Json_NonNumeric_ThrowsJsonException(string malformedJson)
    {
        await Assert.That(() => JsonSerializer.Deserialize<AlterId>(malformedJson))
            .ThrowsExactly<JsonException>();
    }

    [Test]
    public async Task IParsable_InRange_ReturnsAlterId()
    {
        var parsed = AlterId.TryParse("42", provider: null, out var result);

        using (Assert.Multiple())
        {
            await Assert.That(parsed).IsTrue();
            await Assert.That(result.Value).IsEqualTo((short)42);
        }
    }

    [Test]
    [Arguments("32768")]
    [Arguments("-32769")]
    [Arguments("2147483647")]
    [Arguments("not-a-number")]
    public async Task IParsable_OutOfRange_ReturnsFalse(string input)
    {
        var parsed = AlterId.TryParse(input, provider: null, out _);
        await Assert.That(parsed).IsFalse()
            .Because("Route-parameter binding uses TryParse; anything outside short range or non-numeric must yield false so ASP.NET Core surfaces a 400 rather than a runtime overflow.");
    }

}
