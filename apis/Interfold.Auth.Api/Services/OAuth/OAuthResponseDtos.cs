using System.Text.Json.Serialization;

namespace Interfold.Auth.Api.Services.OAuth;

// Typed DTOs for OAuth token/userinfo responses. Callers were previously walking
// JsonDocument roots with TryGetProperty; typing the shapes here means schema drift
// (Google/Discord renames a field, Apple ships a new claim) surfaces as a
// deserialization miss instead of a silent null return.
//
// PropertyNamingPolicy on the shared serializer already snake_cases outbound JSON,
// but these types deserialize inbound provider JSON which uses snake_case field
// names — the [JsonPropertyName] attributes keep the mapping explicit so no future
// policy change silently breaks OAuth.

/// <summary>
/// Body returned by <c>POST https://oauth2.googleapis.com/token</c>
/// (and the Discord equivalent, which shares the same field shape).
/// </summary>
public sealed record OAuthTokenResponse
{
    [JsonPropertyName("access_token")]
    public string? AccessToken { get; init; }

    [JsonPropertyName("token_type")]
    public string? TokenType { get; init; }

    [JsonPropertyName("expires_in")]
    public int? ExpiresIn { get; init; }

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; init; }

    [JsonPropertyName("scope")]
    public string? Scope { get; init; }

    [JsonPropertyName("id_token")]
    public string? IdToken { get; init; }
}

/// <summary>
/// Subset of Google's userinfo v2 response
/// (<c>GET https://www.googleapis.com/oauth2/v2/userinfo</c>) that we actually
/// consume. Extra fields returned by Google are ignored.
/// </summary>
public sealed record GoogleUserInfoResponse
{
    [JsonPropertyName("email")]
    public string? Email { get; init; }

    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("verified_email")]
    public bool? VerifiedEmail { get; init; }
}

/// <summary>
/// Subset of Discord's <c>GET /users/@me</c> response.
/// </summary>
public sealed record DiscordUserResponse
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("username")]
    public string? Username { get; init; }

    [JsonPropertyName("global_name")]
    public string? GlobalName { get; init; }

    [JsonPropertyName("avatar")]
    public string? Avatar { get; init; }
}
