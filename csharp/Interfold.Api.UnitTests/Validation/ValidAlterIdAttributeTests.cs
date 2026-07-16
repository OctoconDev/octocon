using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Interfold.Contracts.Ids;
using Interfold.Contracts.Models.Read;
using Interfold.Contracts.Validation;

namespace Interfold.Api.UnitTests.Validation;

/// <summary>
/// Pure-attribute behaviour for <see cref="ValidAlterIdAttribute"/>. Mirrors the
/// <c>AbsoluteHttpUriAttribute</c> / <c>AbsolutePathAttribute</c> tests in
/// <c>Interfold.Bootstrapper.UnitTests.SharedValidationBoundsTests</c>: exercise the
/// static <see cref="ValidAlterIdAttribute.IsValid"/> core check plus the DataAnnotation
/// wrapper so an accidental range edit trips one fast unit test rather than the
/// controller-side model-binding pipeline.
/// </summary>
public sealed class ValidAlterIdAttributeTests
{
    [Test]
    [Arguments((short)1)]
    [Arguments((short)2)]
    [Arguments((short)1000)]
    [Arguments(short.MaxValue)] // top of the Cassandra smallint column
    public async Task IsValid_AcceptsInRangeIds(short value)
    {
        await Assert.That(ValidAlterIdAttribute.IsValid(new AlterId(value))).IsTrue();
    }

    [Test]
    [Arguments((short)0)]
    [Arguments((short)-1)]
    [Arguments(short.MinValue)]
    public async Task IsValid_RejectsNegativeAndZeroIds(short value)
    {
        // Values outside [short.MinValue, short.MaxValue] are now type-system-blocked
        // via the smallint-typed AlterId constructor, so only null / zero / negatives
        // reach this runtime check. Deserialising an out-of-range JSON number surfaces
        // as a JsonException from AlterIdJsonConverter (see AlterIdJsonTests).
        await Assert.That(ValidAlterIdAttribute.IsValid(new AlterId(value))).IsFalse();
    }

    [Test]
    public async Task IsValid_RejectsNull()
    {
        await Assert.That(ValidAlterIdAttribute.IsValid(null)).IsFalse();
    }

    [Test]
    public async Task Attribute_DefaultAllowNullIsFalse_NullIsRejected()
    {
        var attr = new ValidAlterIdAttribute();
        var ctx = new ValidationContext(new object()) { DisplayName = "AlterId", MemberName = "AlterId" };
        var result = attr.GetValidationResult(null, ctx);
        await Assert.That(result).IsNotNull();
    }

    [Test]
    public async Task Attribute_AllowNull_PassesForNull()
    {
        var attr = new ValidAlterIdAttribute { AllowNull = true };
        var ctx = new ValidationContext(new object()) { DisplayName = "Id", MemberName = "Id" };
        await Assert.That(attr.GetValidationResult(null, ctx)).IsEqualTo(ValidationResult.Success);
    }

    [Test]
    public async Task Attribute_AllowNull_StillRangeChecksNonNull()
    {
        var attr = new ValidAlterIdAttribute { AllowNull = true };
        var ctx = new ValidationContext(new object()) { DisplayName = "Id", MemberName = "Id" };
        var result = attr.GetValidationResult(new AlterId(0), ctx);
        await Assert.That(result).IsNotNull()
            .Because("AllowNull only unlocks the null case; 0 / negative / out-of-range values must still be rejected.");
    }

    [Test]
    public async Task Attribute_AcceptsAlterIdValueTypeDirectly()
    {
        var attr = new ValidAlterIdAttribute();
        var ctx = new ValidationContext(new object()) { DisplayName = "AlterId", MemberName = "AlterId" };
        await Assert.That(attr.GetValidationResult(new AlterId(5), ctx)).IsEqualTo(ValidationResult.Success);
    }

    [Test]
    public async Task Attribute_ErrorMessageIsInvalidAlterIdSentinel()
    {
        // The InvalidModelStateResponseFactory in Program.cs matches this exact string
        // via ValidationErrorCodeRegistry to preserve the wire-visible `invalid_alter_id`
        // ErrorCode. Changing it here without updating the registry silently downgrades
        // the wire code to the generic `bad_request` fallback.
        var attr = new ValidAlterIdAttribute();
        await Assert.That(attr.ErrorMessage).IsEqualTo("Invalid alter ID.");
    }

    // -----------------------------------------------------------------------------
    // Record end-to-end sanity: the request records that carry [ValidAlterId] must
    // (a) round-trip `{"id": 5}` / `{"alter_id": 5}` through System.Text.Json, and
    // (b) expose the attribute on the *primary-constructor parameter* — not on the
    // compiler-generated property — because ASP.NET Core MVC's ObjectModelValidator
    // hard-throws on records with validation metadata on properties
    // (ThrowIfRecordTypeHasValidationOnProperties). Reflecting on the parameter
    // instead of using Validator.TryValidateObject (which only walks properties)
    // mirrors what MVC actually reads at model-binding time.
    // -----------------------------------------------------------------------------

    [Test]
    public async Task FrontStartRequest_JsonWithValidId_DeserializesAndValidatesViaParameterAttribute()
    {
        var opts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
        var req = JsonSerializer.Deserialize<FrontStartRequest>("{\"id\":5}", opts)!;

        await Assert.That(req.Id).IsEqualTo(new AlterId(5));
        await Assert.That(ValidateRecordParameter<FrontStartRequest>("Id", req.Id)).IsTrue();
    }

    [Test]
    public async Task FrontStartRequest_JsonWithMissingId_FailsValidationViaParameterAttribute()
    {
        var opts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
        var req = JsonSerializer.Deserialize<FrontStartRequest>("{}", opts)!;

        await Assert.That(req.Id).IsEqualTo(new AlterId(0));
        await Assert.That(ValidateRecordParameter<FrontStartRequest>("Id", req.Id)).IsFalse()
            .Because("missing id defaults to AlterId(0); ValidAlterId must reject it.");
    }

    [Test]
    public async Task FrontPrimaryRequest_JsonWithNullId_PassesValidationViaParameterAttribute()
    {
        var opts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
        var req = JsonSerializer.Deserialize<FrontPrimaryRequest>("{\"id\":null}", opts)!;

        await Assert.That(req.Id).IsNull();
        await Assert.That(ValidateRecordParameter<FrontPrimaryRequest>("Id", req.Id)).IsTrue()
            .Because("FrontPrimaryRequest uses AllowNull = true to preserve the 'clear primary' semantics.");
    }

    [Test]
    public async Task FrontPrimaryRequest_JsonWithZeroId_FailsValidationViaParameterAttribute()
    {
        var opts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
        var req = JsonSerializer.Deserialize<FrontPrimaryRequest>("{\"id\":0}", opts)!;

        await Assert.That(req.Id).IsEqualTo(new AlterId(0));
        await Assert.That(ValidateRecordParameter<FrontPrimaryRequest>("Id", req.Id)).IsFalse()
            .Because("AllowNull only unlocks null; explicit 0 still trips the range check.");
    }

    [Test]
    public async Task JournalAlterRequest_ParameterCarriesValidAlterIdAttribute()
        // Positive assertion that the attribute is discoverable on the constructor
        // parameter. If the attribute drifts back onto the property this test fails
        // fast, before an integration test surfaces the 500 from
        // ThrowIfRecordTypeHasValidationOnProperties.
        => await Assert.That(GetParameterAttribute<JournalAlterRequest>("AlterId")).IsNotNull();

    [Test]
    public async Task TagAlterRequest_ParameterCarriesValidAlterIdAttribute()
        => await Assert.That(GetParameterAttribute<TagAlterRequest>("AlterId")).IsNotNull();

    /// <summary>
    /// Runs the same check MVC's parameter-binding validator does: pull the
    /// <see cref="ValidAlterIdAttribute"/> off the primary constructor parameter
    /// (not the compiler-generated property) and evaluate it against
    /// <paramref name="value"/>. Falls back to a hard failure if the attribute
    /// isn't there so the caller sees a clear "attribute went missing" signal
    /// rather than a silent success.
    /// </summary>
    private static bool ValidateRecordParameter<T>(string parameterName, object? value)
    {
        var attr = GetParameterAttribute<T>(parameterName);
        if (attr is null)
        {
            return false;
        }

        var ctx = new ValidationContext(new object())
        {
            DisplayName = parameterName,
            MemberName = parameterName
        };
        return attr.GetValidationResult(value, ctx) == ValidationResult.Success;
    }

    private static ValidAlterIdAttribute? GetParameterAttribute<T>(string parameterName)
    {
        var ctor = typeof(T).GetConstructors().First();
        var parameter = ctor.GetParameters().First(p => p.Name == parameterName);
        return (ValidAlterIdAttribute?)parameter
            .GetCustomAttributes(typeof(ValidAlterIdAttribute), inherit: false)
            .FirstOrDefault();
    }
}
