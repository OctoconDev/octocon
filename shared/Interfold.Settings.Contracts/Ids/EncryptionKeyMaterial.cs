using System.Text.Json.Serialization;

namespace Interfold.Contracts.Ids;

/// <summary>
/// The derived per-system encryption key returned by the setup/recover encryption commands
/// (<c>EncryptionCommandResult.Key</c>). Secret material — <c>ToString()</c> redacts; the
/// raw-string converter keeps the persisted command-result JSON and replay hashes identical.
/// </summary>
[JsonConverter(typeof(EncryptionKeyMaterialJsonConverter))]
public readonly record struct EncryptionKeyMaterial
{
    public string Value { get; }

    public EncryptionKeyMaterial(string value)
    {
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }

    public static explicit operator EncryptionKeyMaterial(string value) => new(value);

    public override string ToString() => SecretRedaction.Redact(Value);
}

internal sealed class EncryptionKeyMaterialJsonConverter : StringBackedJsonConverter<EncryptionKeyMaterial>
{
    protected override EncryptionKeyMaterial Create(string value) => new(value);
    protected override string GetValue(EncryptionKeyMaterial value) => value.Value;
}
