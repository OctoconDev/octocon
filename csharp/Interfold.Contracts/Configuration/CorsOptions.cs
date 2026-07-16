namespace Interfold.Contracts.Configuration;

using System.ComponentModel.DataAnnotations;
using Interfold.Contracts.Configuration.Validation;

/// <summary>
/// Strongly-typed options for the API's CORS allow-list. Bound from the
/// <c>OCTOCON_CORS_ALLOWED_ORIGINS</c> comma-separated env var via
/// <c>ConfigurationServiceCollectionExtensions.ApplyCors</c>; trailing slashes
/// are trimmed on parse for parity with ASP.NET Core's CORS matcher (which
/// does exact string matching against
/// <see cref="Microsoft.AspNetCore.Cors.Infrastructure.CorsPolicy.Origins"/>).
/// Blank input yields an empty <see cref="AllowedOrigins"/>; the API's default
/// CORS policy falls back to allow-any when the list is empty (dev-only —
/// production stacks must set the env var explicitly).
///
/// Per-entry <see cref="AbsoluteHttpUriAttribute"/> semantics are enforced via
/// <see cref="IValidatableObject.Validate"/> so an operator that pushes a
/// non-http origin (e.g. a raw hostname, a <c>ws://</c> URL) sees a
/// <c>ValidateOnStart</c> failure with the offending entry called out — the
/// same rule the bootstrapper's <c>ValidateAbsoluteHttpUri</c> enforces on
/// <c>config.apiRuntime.corsAllowedOrigins</c>.
/// </summary>
public sealed class CorsOptions : IValidatableObject
{
    public const string SectionName = "Octocon:Cors";

    /// <summary>
    /// Normalised, de-duplicated allow-list. Values are trimmed, trailing-slash-stripped,
    /// and compared case-insensitively at parse time; use as-is with
    /// <see cref="Microsoft.AspNetCore.Cors.Infrastructure.CorsPolicyBuilder.WithOrigins"/>.
    /// </summary>
    public IReadOnlyList<string> AllowedOrigins { get; set; } = [];

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        for (var i = 0; i < AllowedOrigins.Count; i++)
        {
            var origin = AllowedOrigins[i];
            if (!AbsoluteHttpUriAttribute.IsAbsoluteHttpUri(origin))
            {
                yield return new ValidationResult(
                    $"{nameof(AllowedOrigins)}[{i}]='{origin}' is not a valid absolute http(s) URL.",
                    [$"{nameof(AllowedOrigins)}[{i}]"]);
            }
        }
    }
}
