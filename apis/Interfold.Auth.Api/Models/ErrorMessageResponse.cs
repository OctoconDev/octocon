namespace Interfold.Auth.Api.Models;

/// <summary>
/// Minimal <c>{"error": "..."}</c> body used by the auth-link callback's failure branches.
/// Typed replacement for the previous anonymous object — identical wire shape.
/// </summary>
public sealed record ErrorMessageResponse(string Error);
