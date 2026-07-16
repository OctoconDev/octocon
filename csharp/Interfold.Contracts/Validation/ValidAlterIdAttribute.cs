namespace Interfold.Contracts.Validation;

using System.ComponentModel.DataAnnotations;
using Interfold.Contracts.Ids;

/// <summary>
/// Requires an <see cref="AlterId"/> (or nullable <see cref="AlterId"/>) request-model
/// property to carry a positive, in-range identifier — the same invariant the front
/// command handlers enforce inside the domain (see
/// <c>StartFrontCommandHandler</c>, <c>EndFrontCommandHandler</c>, <c>SetFrontCommandHandler</c>,
/// which reject <c>&lt; 1 or &gt; 32_767</c>). Applied at the wire boundary so a bad payload
/// short-circuits at model binding with a <c>400</c> instead of tripping the handler's
/// invariant guard.
///
/// Mirrors the <c>AbsoluteHttpUriAttribute</c> / <c>AbsolutePathAttribute</c> pattern
/// used by the configuration models: a small pure-logic core (<see cref="IsValid"/>)
/// so tests and other layers can reuse the same "what counts as a valid AlterId on
/// the wire" rule in one place.
///
/// The default <see cref="ValidationAttribute.ErrorMessage"/> is
/// <c>"Invalid alter ID."</c> so the wire response code remains
/// <c>invalid_alter_id</c> when <c>InvalidModelStateResponseFactory</c> maps
/// DataAnnotations failures.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter, AllowMultiple = false)]
public sealed class ValidAlterIdAttribute : ValidationAttribute
{
    /// <summary>
    /// Alter ids are persisted in Cassandra <c>smallint</c> columns; anything above
    /// <see cref="short.MaxValue"/> cannot round-trip. Retyped as <see cref="short"/>
    /// alongside the narrowing of <see cref="AlterId.Value"/> — the upper-bound check
    /// against <see cref="AlterId.Value"/> is now a tautology (the type system enforces
    /// it), retained for the null-and-negative case that the type cannot express.
    /// </summary>
    public const short MaxValue = short.MaxValue;

    /// <summary>Minimum accepted alter id. Zero and negatives are always rejected.</summary>
    public const short MinValue = 1;

    public ValidAlterIdAttribute()
    {
        ErrorMessage = "Invalid alter ID.";
    }

    /// <summary>
    /// When <c>true</c>, a null <see cref="AlterId"/> is accepted (used by
    /// <c>FrontPrimaryRequest</c> where <c>null</c> legitimately clears the primary
    /// front). Non-null values are still range-checked. Defaults to <c>false</c>.
    /// </summary>
    public bool AllowNull { get; init; }

    /// <inheritdoc />
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

        /*TODO: When .NET 11 comes out, readd the actual DB check here! (Need Async support)
          happy to lose this guard in the short term*/
        return IsValid(alterId)
            ? ValidationResult.Success
            : new ValidationResult(ErrorMessage, [memberName]);
    }

    /// <summary>
    /// Pure-logic core used by this attribute and available for reuse (tests,
    /// command handlers that want a single source of truth for the AlterId range).
    /// A <c>null</c> value returns <c>false</c> — callers that treat null as "unset"
    /// should short-circuit before calling this.
    /// </summary>
    public static bool IsValid(AlterId? value)
    {
        if (value is not { } id)
        {
            return false;
        }

        return id.Value is >= MinValue and <= MaxValue;
    }
}
