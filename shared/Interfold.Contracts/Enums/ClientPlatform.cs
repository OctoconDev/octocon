using System.Text.Json;
using System.Text.Json.Serialization;

namespace Interfold.Contracts.Enums;

/// <summary>
/// The client platform identifiers used by the firebase-config endpoint's
/// <c>?platform=</c> query value and the socket join payload's <c>platform</c> member.
/// Wire spellings are the lowercase names ("android" / "ios" / "web"); unknown values are
/// handled by callers via <see cref="EnumWire{TEnum}.TryParse"/> so the legacy
/// invalid-platform responses are preserved.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ClientPlatform>))]
public enum ClientPlatform
{
    [JsonStringEnumMemberName("android")]
    Android,

    [JsonStringEnumMemberName("ios")]
    Ios,

    [JsonStringEnumMemberName("web")]
    Web,
}