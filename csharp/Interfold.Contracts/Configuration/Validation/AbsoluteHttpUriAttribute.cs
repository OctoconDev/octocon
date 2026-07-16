namespace Interfold.Contracts.Configuration.Validation;

using System.ComponentModel.DataAnnotations;

/// <summary>
/// Requires a string configuration value to parse as an absolute HTTP or HTTPS URI
/// (see <see cref="Uri.TryCreate(string, UriKind, out Uri)"/> with
/// <see cref="UriKind.Absolute"/>). Null and empty values are accepted by default so
/// this attribute stacks cleanly on optional properties like <c>OtlpEndpoint</c> and
/// <c>AvatarPublicBase</c>. Combine with <see cref="RequiredAttribute"/> when the
/// value is mandatory.
///
/// Mirrors the bootstrapper's <c>ValidateAbsoluteHttpUri</c>/<c>ValidateOptionalAbsoluteHttpUri</c>
/// helpers so both sides of the bootstrap → API boundary enforce the same "absolute
/// http(s) URL" contract in identical language.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false)]
public sealed class AbsoluteHttpUriAttribute : ValidationAttribute
{
    /// <summary>
    /// When <c>true</c>, whitespace-only strings are treated the same as empty (skipped).
    /// Defaults to <c>true</c> to match .NET's environment-variables provider surfacing
    /// unset OCTOCON_* variables as empty strings rather than null.
    /// </summary>
    public bool AllowEmpty { get; init; } = true;

    /// <inheritdoc />
    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
    {
        if (value is null)
        {
            return ValidationResult.Success;
        }

        if (value is not string s)
        {
            return new ValidationResult(
                $"{validationContext.DisplayName} must be a string absolute http(s) URL (got {value.GetType().Name}).",
                [validationContext.MemberName ?? validationContext.DisplayName]);
        }

        if (AllowEmpty && string.IsNullOrWhiteSpace(s))
        {
            return ValidationResult.Success;
        }

        if (string.IsNullOrWhiteSpace(s))
        {
            return new ValidationResult(
                $"{validationContext.DisplayName} must be a non-empty absolute http(s) URL.",
                [validationContext.MemberName ?? validationContext.DisplayName]);
        }

        if (!IsAbsoluteHttpUri(s))
        {
            return new ValidationResult(
                $"{validationContext.DisplayName}='{s}' is not a valid absolute http(s) URL.",
                [validationContext.MemberName ?? validationContext.DisplayName]);
        }

        return ValidationResult.Success;
    }

    /// <summary>
    /// Pure-logic core check reused by both this <see cref="ValidationAttribute"/> and the
    /// bootstrapper's runtime validator so a change to the "what counts as absolute http(s)"
    /// rule lands on both sides in one place. Whitespace/empty returns <c>false</c>; callers
    /// that treat blank as "field disabled" should short-circuit before calling this.
    /// </summary>
    public static bool IsAbsoluteHttpUri(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;
    }
}
