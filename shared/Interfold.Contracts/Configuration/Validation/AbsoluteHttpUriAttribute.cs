namespace Interfold.Contracts.Configuration.Validation;

using System.ComponentModel.DataAnnotations;

/// <summary>Requires an absolute http/https URI string. Null/empty is accepted by default;
/// combine with <c>[Required]</c> when mandatory. Mirrors the bootstrapper's
/// <c>ValidateAbsoluteHttpUri</c> helpers so both sides enforce the same contract.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false)]
public sealed class AbsoluteHttpUriAttribute : ValidationAttribute
{
    /// <summary>Whitespace-only counts as empty when true (default).</summary>
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

    /// <summary>Pure-logic core check reused by the bootstrapper's runtime validator.
    /// Whitespace/empty → false; callers that treat blank as "disabled" short-circuit first.</summary>
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
