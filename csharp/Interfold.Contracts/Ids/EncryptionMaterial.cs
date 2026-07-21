using System.Security.Cryptography;
using System.Text.Json;
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

    /// <summary>
    /// Explicit narrow so call sites can write <c>(EncryptionKeyMaterial)raw</c> instead of
    /// <c>new EncryptionKeyMaterial(raw)</c>. No implicit widen by design —
    /// see <see cref="DiscordId"/>'s operator xml-doc for the redaction-preservation rationale.
    /// </summary>
    public static explicit operator EncryptionKeyMaterial(string value) => new(value);

    public override string ToString() => SecretRedaction.Redact(Value);
}

internal sealed class EncryptionKeyMaterialJsonConverter : StringBackedJsonConverter<EncryptionKeyMaterial>
{
    protected override EncryptionKeyMaterial Create(string value) => new(value);
    protected override string GetValue(EncryptionKeyMaterial value) => value.Value;
}

/// <summary>
/// Checksum of the derived encryption key persisted to <c>encryption_state.key_checksum</c>
/// and compared on recover/import. <c>ToString()</c> redacts.
/// </summary>
public readonly record struct KeyChecksum
{
    public string Value { get; }

    public KeyChecksum(string value)
    {
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// Explicit narrow so call sites can write <c>(KeyChecksum)raw</c> instead of
    /// <c>new KeyChecksum(raw)</c>. No implicit widen by design — see <see cref="DiscordId"/>'s
    /// operator xml-doc for the redaction-preservation rationale. Prefer
    /// <see cref="FromNullable(string?)"/> when the DB read may be null.
    /// </summary>
    public static explicit operator KeyChecksum(string value) => new(value);

    public override string ToString() => SecretRedaction.Redact(Value);

    /// <summary>Null-preserving wrap for DB/state reads.</summary>
    public static KeyChecksum? FromNullable(string? value) => value is null ? null : new KeyChecksum(value);
}

/// <summary>
/// Per-system key-derivation salt persisted to <c>encryption_state.salt</c>. Not secret in
/// the cryptographic sense, but redacted anyway so state dumps don't hand out derivation
/// inputs alongside checksums.
/// </summary>
public readonly record struct EncryptionSalt
{
    /// <summary>
    /// Width of a freshly-minted salt in raw bytes before base64 encoding. Centralised so
    /// <see cref="NewRandom"/> is the single source of truth for salt width.
    /// </summary>
    private const int RandomByteWidth = 32;

    public string Value { get; }

    public EncryptionSalt(string value)
    {
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// Explicit narrow so call sites can write <c>(EncryptionSalt)raw</c> instead of
    /// <c>new EncryptionSalt(raw)</c>. No implicit widen by design — see <see cref="DiscordId"/>'s
    /// operator xml-doc for the redaction-preservation rationale. Prefer
    /// <see cref="FromNullable(string?)"/> for nullable DB reads and <see cref="NewRandom"/>
    /// to mint a fresh salt in one call.
    /// </summary>
    public static explicit operator EncryptionSalt(string value) => new(value);

    public override string ToString() => SecretRedaction.Redact(Value);

    /// <summary>Null-preserving wrap for DB/state reads.</summary>
    public static EncryptionSalt? FromNullable(string? value) => value is null ? null : new EncryptionSalt(value);

    /// <summary>
    /// Mint a fresh cryptographically-random salt (32 bytes → base64) and wrap it in one
    /// call. The raw base64 string spends zero time as a bare local: <see cref="ToString"/>
    /// redacts, a bare <see cref="string"/> would not.
    /// </summary>
    public static EncryptionSalt NewRandom()
        => new(Convert.ToBase64String(RandomNumberGenerator.GetBytes(RandomByteWidth)));
}
