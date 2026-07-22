using System.Text.Json.Serialization;

namespace Interfold.Contracts.Enums;

/// <summary>Poll type. Wire: lowercase name. Storage: smallint 0/1/2. Persistence read uses
/// fallback-less <c>FromCode&lt;PollType&gt;()</c> so corrupt rows throw.</summary>
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
