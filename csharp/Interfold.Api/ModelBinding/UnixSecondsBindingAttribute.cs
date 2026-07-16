namespace Interfold.Api.ModelBinding;

/// <summary>
/// Marker attribute paired with <c>[FromQuery(Name = "…")]</c> on a
/// <see cref="Models.UnixSeconds"/> action parameter to control the wire-visible
/// <c>ErrorResponse</c> emitted when the request value fails to parse. The
/// <see cref="UnixSecondsModelBinder"/> reads <see cref="ErrorMessage"/> and adds it
/// verbatim as the <see cref="Microsoft.AspNetCore.Mvc.ModelBinding.ModelError"/>
/// message, then stashes <see cref="ErrorCode"/> on
/// <see cref="Microsoft.AspNetCore.Http.HttpContext.Items"/> under
/// <see cref="ItemsKey(string)"/> so the shared
/// <c>InvalidModelStateResponseFactory</c> (Program.cs) can prefer it over the
/// message-keyed <c>ValidationErrorCodeRegistry</c> lookup.
///
/// <para>
/// Intentionally an inert marker — it does NOT implement
/// <see cref="Microsoft.AspNetCore.Mvc.ModelBinding.IBindingSourceMetadata"/>, so
/// the accompanying <c>[FromQuery(Name = …)]</c> still drives the value source.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
internal sealed class UnixSecondsBindingAttribute : Attribute
{
    /// <summary>
    /// The <c>ErrorResponse.Code</c> string to emit on parse failure (e.g.
    /// <c>"invalid_end_anchor"</c>, <c>"invalid_anchor"</c>). Kept as a raw string so
    /// the attribute compiles as a constant expression and stays independent of the
    /// <c>Interfold.Contracts.ErrorCodes</c> registry ordering.
    /// </summary>
    public required string ErrorCode { get; init; }

    /// <summary>
    /// The human-readable <c>ErrorResponse.Error</c> message to emit on parse failure
    /// (e.g. <c>"Invalid end anchor. Please pass a valid Unix timestamp."</c>). Written
    /// verbatim as the <see cref="Microsoft.AspNetCore.Mvc.ModelBinding.ModelError"/>
    /// message so callers observe byte-identical wording to the pre-binder behaviour.
    /// </summary>
    public required string ErrorMessage { get; init; }

    /// <summary>
    /// Builds the <see cref="Microsoft.AspNetCore.Http.HttpContext.Items"/> key the
    /// binder uses to stash <see cref="ErrorCode"/> for
    /// <paramref name="fieldName"/>. Kept public+static so
    /// <c>InvalidModelStateResponseFactory</c> can compute the same key without a
    /// second dependency edge.
    /// </summary>
    public static string ItemsKey(string fieldName) => $"UnixSecondsBinding::{fieldName}";
}
