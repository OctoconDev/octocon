using System.Text.Json.Serialization;

namespace Interfold.Shared.Contracts.Enums;

/// <summary>
/// Per-alter visibility tier. Values are wire-stable (JSON string form matches the
/// enum member name) and Scylla-stable (persisted as <c>smallint</c> with the ordinal
/// below), so re-ordering members is a breaking change for both persisted rows and
/// deployed clients.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<VisibilityLevel>))]
public enum VisibilityLevel : short
{
    [JsonStringEnumMemberName("public")]
    Public = 0,
    [JsonStringEnumMemberName("friends_only")]
    FriendsOnly = 1,
    [JsonStringEnumMemberName("trusted_only")]
    TrustedOnly = 2,
    [JsonStringEnumMemberName("private")]
    Private = 3
}
