namespace Interfold.Shared.Contracts.Configuration;

using System.ComponentModel.DataAnnotations;
using Interfold.Shared.Contracts.Configuration.Validation;

/// <summary>API CORS allow-list bound from <c>OCTOCON_CORS_ALLOWED_ORIGINS</c>. Trailing
/// slashes are trimmed on parse for parity with ASP.NET Core's exact-string CORS matcher.
/// Empty list falls back to allow-any (dev-only). Per-entry absolute-http validation
/// runs at ValidateOnStart.</summary>
public sealed class CorsOptions : IValidatableObject
{
    public const string SectionName = "Octocon:Cors";

    /// <summary>Normalised, de-duplicated allow-list; pass to <c>WithOrigins</c> as-is.</summary>
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
