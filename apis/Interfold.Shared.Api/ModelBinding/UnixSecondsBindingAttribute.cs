namespace Interfold.Shared.Api.ModelBinding;

/// <summary>Inert marker paired with <c>[FromQuery(Name = "…")]</c> on
/// <see cref="Models.UnixSeconds"/> parameters. Controls the wire-visible
/// <c>ErrorResponse</c> on parse failure: <see cref="UnixSecondsModelBinder"/> writes
/// <see cref="ErrorMessage"/> as the ModelError message and stashes
/// <see cref="ErrorCode"/> on <c>HttpContext.Items</c> under <see cref="ItemsKey(string)"/>,
/// which the InvalidModelStateResponseFactory prefers over the message-keyed registry.</summary>
[AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
internal sealed class UnixSecondsBindingAttribute : Attribute
{
    /// <summary>Wire <c>ErrorResponse.Code</c> emitted on parse failure (e.g.
    /// <c>invalid_end_anchor</c>). Raw string keeps the attribute constant-expression-friendly.</summary>
    public required string ErrorCode { get; init; }

    /// <summary>Human-readable <c>ErrorResponse.Error</c> emitted on parse failure.</summary>
    public required string ErrorMessage { get; init; }

    /// <summary>Shared key generator for the <c>HttpContext.Items</c> stash.</summary>
    public static string ItemsKey(string fieldName) => $"UnixSecondsBinding::{fieldName}";
}
