using System.Text.Json.Serialization;

namespace Interfold.Shared.Contracts.Enums;

/// <summary>
/// Outcome verb carried on <c>SettingsFieldCommandResult.Action</c>. Single-member today
/// ("field_created"), typed as an enum for consistency with the other command-result actions.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<SettingsFieldAction>))]
public enum SettingsFieldAction
{
    [JsonStringEnumMemberName("field_created")]
    FieldCreated,
}
