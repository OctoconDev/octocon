using System.Text.Json.Serialization;

namespace Interfold.Contracts.Enums;

/// <summary>
/// Poll type. Wire representation is the lowercase name (<c>"vote"</c>, <c>"choice"</c>,
/// <c>"approval"</c>), preserving compatibility with the pre-existing Elixir payloads
/// and the Kotlin client.
///
/// <para>
/// The Scylla schema stores this as a <c>smallint</c>: <c>0 → vote</c>, <c>1 → choice</c>,
/// <c>2 → approval</c>. Callers cast <c>(short)value</c> outbound and use the fallback-less
/// <c>code.FromCode&lt;PollType&gt;()</c> inbound; unknown or null on-disk codes throw
/// <see cref="ArgumentOutOfRangeException"/> so corrupt/stale rows surface at the read site
/// instead of silently coercing to <c>Vote</c>.
/// </para>
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<PollType>))]
public enum PollType : short
{
    [JsonStringEnumMemberName("vote")]
    Vote = 0,

    [JsonStringEnumMemberName("choice")]
    Choice = 1,

    [JsonStringEnumMemberName("approval")]
    Approval = 2,
}