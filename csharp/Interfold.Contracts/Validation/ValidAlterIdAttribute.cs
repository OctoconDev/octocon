namespace Interfold.Contracts.Validation;

using System.ComponentModel.DataAnnotations;
using Interfold.Contracts.Ids;

/// <summary>
/// Requires an <see cref="AlterId"/> request-model property to carry a positive,
/// in-range identifier. Default <see cref="ValidationAttribute.ErrorMessage"/>
/// <c>"Invalid alter ID."</c> so <c>InvalidModelStateResponseFactory</c> maps to the
/// <c>invalid_alter_id</c> wire code.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter, AllowMultiple = false)]
public sealed class ValidAlterIdAttribute : ValidationAttribute
{
    // Cassandra smallint column ceiling. Now a tautology after AlterId.Value narrowed
    // to short; retained for the null/negative case that the type can't express.
    public const short MaxValue = short.MaxValue;

    public const short MinValue = 1;

    public ValidAlterIdAttribute()
    {
        ErrorMessage = "Invalid alter ID.";
    }

    /// <summary>When <c>true</c>, a null <see cref="AlterId"/> is accepted (e.g.
    /// <c>FrontPrimaryRequest</c> clears the primary front); non-null values still range-checked.</summary>
    public bool AllowNull { get; init; }

    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
    {
        var memberName = validationContext.MemberName ?? validationContext.DisplayName;

        if (value is null)
        {
            return AllowNull
                ? ValidationResult.Success
                : new ValidationResult(ErrorMessage, [memberName]);
        }

        AlterId? alterId = value switch
        {
            AlterId id => id,
            _ => null
        };

        if (alterId is null)
        {
            return new ValidationResult(ErrorMessage, [memberName]);
        }

        return IsValid(alterId)
            ? ValidationResult.Success
            : new ValidationResult(ErrorMessage, [memberName]);
    }

    // Pure-logic core reusable by tests / command handlers that want one source of
    // truth for the AlterId range. Null returns false — treat unset as short-circuit.
    public static bool IsValid(AlterId? value)
    {
        if (value is not { } id)
        {
            return false;
        }

        return id.Value is >= MinValue and <= MaxValue;
    }
}
