using System.Text.Json.Serialization;

namespace Interfold.Shared.Contracts.Enums;

/// <summary>
/// Discriminator for the hosting source of an avatar URL.
/// Stored as <c>smallint</c> in Scylla, exposed on the wire as snake-case strings
/// for parity with <see cref="Interfold.Shared.Contracts.Models.VisibilityLevel"/>.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<AvatarSource>))]
public enum AvatarSource : short
{
    /// <summary>
    /// Avatar bytes are persisted by <c>IAvatarStorage</c> on this deployment;
    /// the stored URL is a relative path that requires origin qualification when
    /// returned to a client.
    /// </summary>
    [JsonStringEnumMemberName("local")]
    Local = 0,

    /// <summary>
    /// Avatar lives on a third-party host. The stored URL is absolute and is
    /// returned to the client unchanged; cleanup paths must not delete it.
    /// </summary>
    [JsonStringEnumMemberName("external")]
    External = 1,
}
