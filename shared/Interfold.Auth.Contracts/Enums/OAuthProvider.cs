using System.Text.Json.Serialization;

namespace Interfold.Contracts.Enums;

/// <summary>
/// The supported external OAuth identity providers. Route segments and socket/cluster event
/// payloads carry the lowercase wire spellings ("discord" / "google" / "apple"); controllers
/// bind the route value as a raw string and feed it through <see cref="EnumWire{TEnum}.TryParse"/>
/// so unknown providers still produce the legacy "unsupported provider" response rather than
/// a model-binding 400.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<OAuthProvider>))]
public enum OAuthProvider
{
    [JsonStringEnumMemberName("discord")]
    Discord,

    [JsonStringEnumMemberName("google")]
    Google,

    [JsonStringEnumMemberName("apple")]
    Apple,
}
