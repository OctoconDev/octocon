using System.Text.Json.Serialization;

namespace Interfold.Contracts.Enums;

/// <summary>
/// Type of a user-defined settings field. The wire representation is the lowercase
/// snake_case name (<c>"text"</c>, <c>"month_year"</c>, <c>"month_day"</c>, ...),
/// preserving what the Elixir server emitted so the Kotlin client and any prior
/// captured payloads deserialize unchanged.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<FieldType>))]
public enum FieldType : short
{
    [JsonStringEnumMemberName("text")]
    Text = 0,

    [JsonStringEnumMemberName("number")]
    Number = 1,

    [JsonStringEnumMemberName("boolean")]
    Boolean = 2,

    [JsonStringEnumMemberName("date")]
    Date = 3,

    [JsonStringEnumMemberName("colour")]
    Colour = 4,

    [JsonStringEnumMemberName("plaintext")]
    Plaintext = 5,

    [JsonStringEnumMemberName("month")]
    Month = 6,

    [JsonStringEnumMemberName("year")]
    Year = 7,

    [JsonStringEnumMemberName("month_year")]
    MonthYear = 8,

    [JsonStringEnumMemberName("timestamp")]
    Timestamp = 9,

    [JsonStringEnumMemberName("month_day")]
    MonthDay = 10,
}