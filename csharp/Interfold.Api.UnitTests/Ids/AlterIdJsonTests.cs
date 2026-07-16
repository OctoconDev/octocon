using System.Text.Json;
using Interfold.Contracts.Ids;

namespace Interfold.Api.UnitTests.Ids;

/// <summary>
/// Pins the JSON contract for <see cref="AlterId"/> now that the underlying value is a
/// <see cref="short"/>: the wire remains a JSON number, round-trips in the smallint range
/// are byte-identical to the pre-narrowing shape, and out-of-<c>short</c>-range numbers
/// surface as a <see cref="JsonException"/> at the byte boundary (ASP.NET Core maps that
/// to a 400 before the controller runs). This closes the previous gap where a payload
/// like <c>{"id": 40000}</c> deserialised into an <see cref="AlterId"/> and only got
/// caught downstream by <see cref="Interfold.Contracts.Validation.ValidAlterIdAttribute"/>.
/// </summary>
public sealed class AlterIdJsonTests
{
    [Test]
    [Arguments((short)1)]
    [Arguments((short)1000)]
    [Arguments(short.MaxValue)]
    [Arguments((short)0)]           // zero survives the type boundary; ValidAlterIdAttribute rejects it later.
    [Arguments((short)-1)]          // negatives round-trip too; only the range validator objects.
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

    /// <summary>
    /// Any number outside <see cref="short"/> range must be rejected by
    /// <c>Utf8JsonReader.GetInt16</c>. That's the boundary at which the strict range
    /// invariant now lives — no downstream handler needs a
    /// <c>&gt; 32_767</c> guard for the "escaped the type system" case.
    /// </summary>
    [Test]
    [Arguments("32768")]              // one past short.MaxValue
    [Arguments("-32769")]             // one past short.MinValue
    [Arguments("2147483647")]         // int.MaxValue
    [Arguments("-2147483648")]        // int.MinValue
    [Arguments("9999999999999")]      // > int, still finite
    public async Task Json_OutOfShortRange_ThrowsJsonException(string numberLiteral)
    {
        await Assert.That(() => JsonSerializer.Deserialize<AlterId>(numberLiteral))
            .ThrowsExactly<JsonException>()
            .Because("The JSON reader must reject anything outside short range at the byte boundary — ASP.NET Core maps this to a 400 before the model-binding validator ever runs.");
    }

    [Test]
    [Arguments("\"5\"")]     // string, not a number
    [Arguments("null")]      // AlterId is not nullable
    [Arguments("true")]      // bool
    [Arguments("[1]")]       // array
    public async Task Json_NonNumeric_ThrowsJsonException(string malformedJson)
    {
        await Assert.That(() => JsonSerializer.Deserialize<AlterId>(malformedJson))
            .ThrowsExactly<JsonException>();
    }

    // ---------------- IParsable (route binding) -----------------------------

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
