using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models.Read;
using Interfold.Shared.Contracts.Validation;

namespace Interfold.Api.UnitTests.Validation;

// Pure-attribute behaviour for ValidAlterIdAttribute. Mirrors the AbsoluteHttpUri /
// AbsolutePath tests in Interfold.Bootstrapper.UnitTests: exercises IsValid + the
// DataAnnotation wrapper so an accidental range edit trips a fast unit test.
public sealed class ValidAlterIdAttributeTests
{
    [Test]
    [Arguments((short)1)]
    [Arguments((short)2)]
    [Arguments((short)1000)]
    [Arguments(short.MaxValue)]
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
        // Out-of-range JSON numbers surface as a JsonException in AlterIdJsonConverter;
        // only null / zero / negatives reach this runtime check.
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

    // InvalidModelStateResponseFactory matches this exact string via
    // ValidationErrorCodeRegistry to preserve the wire-visible `invalid_alter_id` code.
    [Test]
    public async Task Attribute_ErrorMessageIsInvalidAlterIdSentinel()
    {
        var attr = new ValidAlterIdAttribute();
        await Assert.That(attr.ErrorMessage).IsEqualTo("Invalid alter ID.");
    }

    // MVC hard-throws on records with validation metadata on properties
    // (ThrowIfRecordTypeHasValidationOnProperties); reflect the primary-constructor
    // parameter here to mirror what MVC reads at model-binding time.

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

    // Fails fast if the attribute drifts back onto the property (would surface as 500
    // from ThrowIfRecordTypeHasValidationOnProperties in an integration test).
    [Test]
    public async Task JournalAlterRequest_ParameterCarriesValidAlterIdAttribute()
        => await Assert.That(GetParameterAttribute<JournalAlterRequest>("AlterId")).IsNotNull();

    [Test]
    public async Task TagAlterRequest_ParameterCarriesValidAlterIdAttribute()
        => await Assert.That(GetParameterAttribute<TagAlterRequest>("AlterId")).IsNotNull();

    // Mirrors MVC's parameter-binding validator: attribute off the constructor parameter,
    // not the compiler-generated property. Missing attribute → false (loud signal).
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
