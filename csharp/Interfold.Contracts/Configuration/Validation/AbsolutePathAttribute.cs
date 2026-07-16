namespace Interfold.Contracts.Configuration.Validation;

using System.ComponentModel.DataAnnotations;
using System.IO;

/// <summary>
/// Requires a string configuration value to be an absolute filesystem path
/// (<see cref="Path.IsPathRooted(string)"/>). Null and empty values are accepted (use
/// <see cref="RequiredAttribute"/> when the value is mandatory) so this attribute stacks
/// cleanly on optional properties like <c>AvatarStorageRoot</c>.
///
/// Mirrors the bootstrapper's <c>ValidateOptionalAbsoluteHttpUri</c> sibling for URL
/// values: the pair enforces the same "operator gave us a rooted-path or an absolute
/// http(s) URL" contract on both sides of the bootstrap → API boundary.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false)]
public sealed class AbsolutePathAttribute : ValidationAttribute
{
    /// <summary>
    /// When <c>true</c>, whitespace-only strings are treated the same as empty (skipped).
    /// Defaults to <c>true</c> to match the .NET
    /// <see cref="Microsoft.Extensions.Configuration.EnvironmentVariables.EnvironmentVariablesConfigurationProvider"/>
    /// convention where <c>OCTOCON_FOO=</c> surfaces as an empty string rather than null.
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

    /// <summary>
    /// Pure-logic core check reused by both this <see cref="ValidationAttribute"/> and the
    /// bootstrapper's runtime validator so a change to the "what counts as absolute" rule
    /// lands on both sides in one place. Whitespace/empty returns <c>false</c>; callers
    /// that treat blank as "field disabled" should short-circuit before calling this.
    /// </summary>
    public static bool IsAbsolutePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return Path.IsPathRooted(value);
    }
}
