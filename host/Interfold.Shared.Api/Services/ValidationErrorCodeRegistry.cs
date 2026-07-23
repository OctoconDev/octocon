using System.Collections.Frozen;
using Interfold.Shared.Contracts;

namespace Interfold.Api.Services;

/// <summary>
/// Message-to-<see cref="ErrorCode"/> lookup used by the
/// <c>InvalidModelStateResponseFactory</c> in <c>Program.cs</c> to preserve the
/// wire-visible <c>ErrorResponse.Code</c> for DataAnnotations validation failures.
///
/// <para>
/// Rationale: <see cref="Microsoft.AspNetCore.Mvc.ModelBinding.ModelStateDictionary"/>
/// does not preserve <see cref="System.ComponentModel.DataAnnotations.ValidationResult"/>
/// subclasses — only the resulting <c>ErrorMessage</c> string. Any strategy that
/// keeps the current <c>ErrorResponse { code: "invalid_alter_id" }</c> shape has
/// to funnel through the message. Each <see cref="System.ComponentModel.DataAnnotations.ValidationAttribute"/>
/// that wants a stable wire code registers a
/// <c>"human-readable message" → ErrorCode</c> pair here; unknown messages fall
/// back to the generic <see cref="ErrorCodes.BadRequest"/>.
/// </para>
/// </summary>
internal static class ValidationErrorCodeRegistry
{
    private static readonly FrozenDictionary<string, ErrorCode> Map =
        new Dictionary<string, ErrorCode>(StringComparer.Ordinal)
        {
            ["Invalid alter ID."] = ErrorCodes.InvalidAlterId,
        }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>
    /// Returns the <see cref="ErrorCode"/> registered for
    /// <paramref name="errorMessage"/>, or <see cref="ErrorCodes.BadRequest"/> when
    /// the message is null, empty, or not registered.
    /// </summary>
    public static ErrorCode LookupOrDefault(string? errorMessage)
    {
        if (string.IsNullOrEmpty(errorMessage))
        {
            return ErrorCodes.BadRequest;
        }

        return Map.TryGetValue(errorMessage, out var code) ? code : ErrorCodes.BadRequest;
    }
}
