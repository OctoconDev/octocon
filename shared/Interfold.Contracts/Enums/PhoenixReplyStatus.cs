using System.Text.Json.Serialization;

namespace Interfold.Contracts.Enums;

/// <summary>
/// Reply status carried by a Phoenix channel reply frame. Wire values are
/// <c>"ok"</c> / <c>"error"</c> and MUST NOT change — the Kotlin client and the
/// legacy Elixir server pinned this two-value contract.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<PhoenixReplyStatus>))]
public enum PhoenixReplyStatus
{
    [JsonStringEnumMemberName("ok")]
    Ok,

    [JsonStringEnumMemberName("error")]
    Error,
}
