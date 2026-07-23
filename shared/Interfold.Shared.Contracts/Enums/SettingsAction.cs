using System.Text.Json.Serialization;

namespace Interfold.Shared.Contracts.Enums;

/// <summary>
/// Outcome verb carried on <c>SettingsCommandResult.Action</c>. Serialized into persisted
/// idempotency outcome payloads and HTTP response bodies; the snake-case-lower wire spellings
/// ("description_updated", "avatar_uploaded", …) are frozen.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<SettingsAction>))]
public enum SettingsAction
{
    [JsonStringEnumMemberName("description_updated")]
    DescriptionUpdated,

    [JsonStringEnumMemberName("encryption_reset")]
    EncryptionReset,

    [JsonStringEnumMemberName("push_token_added")]
    PushTokenAdded,

    [JsonStringEnumMemberName("push_token_removed")]
    PushTokenRemoved,

    [JsonStringEnumMemberName("avatar_uploaded")]
    AvatarUploaded,

    [JsonStringEnumMemberName("avatar_deleted")]
    AvatarDeleted,

    [JsonStringEnumMemberName("account_deleted")]
    AccountDeleted,

    [JsonStringEnumMemberName("tags_wiped")]
    TagsWiped,

    [JsonStringEnumMemberName("alters_wiped")]
    AltersWiped,

    [JsonStringEnumMemberName("email_unlinked")]
    EmailUnlinked,

    [JsonStringEnumMemberName("apple_unlinked")]
    AppleUnlinked,

    [JsonStringEnumMemberName("discord_unlinked")]
    DiscordUnlinked,

    [JsonStringEnumMemberName("field_updated")]
    FieldUpdated,

    [JsonStringEnumMemberName("field_relocated")]
    FieldRelocated,

    [JsonStringEnumMemberName("field_deleted")]
    FieldDeleted,
}
