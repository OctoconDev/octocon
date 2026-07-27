using System.Text.Json.Serialization;

namespace Interfold.Shared.Contracts.Enums;

/// <summary>
/// Outcome verb carried on <c>EncryptionCommandResult.Action</c>. Serialized into persisted
/// idempotency outcome payloads and HTTP response bodies; wire spellings
/// ("encryption_setup" / "encryption_recovered") are frozen.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<EncryptionAction>))]
public enum EncryptionAction
{
    [JsonStringEnumMemberName("encryption_setup")]
    EncryptionSetup,

    [JsonStringEnumMemberName("encryption_recovered")]
    EncryptionRecovered,
}
