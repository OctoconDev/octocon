namespace Interfold.Contracts.Configuration.Validation;

using System.ComponentModel.DataAnnotations;
using System.IO;

/// <summary>Requires an absolute filesystem path. Null/empty is accepted; combine with
/// <c>[Required]</c> when mandatory. Mirrors the bootstrapper's runtime path validator.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false)]
public sealed class AbsolutePathAttribute : ValidationAttribute
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
                $"{validationContext.DisplayName} must be a string absolute path (got {value.GetType().Name}).",
                [validationContext.MemberName ?? validationContext.DisplayName]);
        }

        if (AllowEmpty && string.IsNullOrWhiteSpace(s))
        {
            return ValidationResult.Success;
        }

        if (string.IsNullOrWhiteSpace(s))
        {
            return new ValidationResult(
                $"{validationContext.DisplayName} must be a non-empty absolute path.",
                [validationContext.MemberName ?? validationContext.DisplayName]);
        }

        if (!IsAbsolutePath(s))
        {
            return new ValidationResult(
                $"{validationContext.DisplayName}='{s}' must be an absolute path " +
                "(e.g. '/var/lib/interfold/avatars').",
                [validationContext.MemberName ?? validationContext.DisplayName]);
        }

        return ValidationResult.Success;
    }

    /// <summary>Pure-logic core check reused by the bootstrapper. Whitespace/empty → false.</summary>
    public static bool IsAbsolutePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return Path.IsPathRooted(value);
    }
}
