using System.Text.Json.Serialization;

namespace Interfold.Api.Helpers;

/// <summary>
/// Minimal shape of a decoded JWS/JOSE header (RFC 7515 §4.1). Only the <c>alg</c>
/// member is consulted by the two custom ES256 signature validators (HTTP auth pipeline
/// and WebSocket join path); every other header field is ignored intentionally.
/// </summary>
/// <remarks>
/// Named <c>JwsHeader</c> rather than <c>JwtHeader</c> to avoid a collision with
/// <see cref="System.IdentityModel.Tokens.Jwt.JwtHeader"/>, which is transitively imported
/// by the socket handler.
/// </remarks>
internal sealed record JwsHeader
{
    /// <summary>The only signature algorithm Interfold accepts.</summary>
    public const string Es256 = "ES256";

    [JsonPropertyName("alg")]
    public string? Alg { get; init; }
}
