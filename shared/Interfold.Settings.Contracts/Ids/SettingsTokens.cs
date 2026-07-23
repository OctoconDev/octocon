using System.Text.Json.Serialization;

namespace Interfold.Contracts.Ids;

/// <summary>
/// Device push-notification registration token (FCM/APNs). Carried on
/// <c>AddPushTokenCommand</c>/<c>RemovePushTokenCommand</c> whose payloads are persisted and
/// hashed by the idempotency store — the converter emits the raw string so stored hashes
/// remain valid. <c>ToString()</c> redacts via <see cref="SecretRedaction"/> on spine.
/// </summary>
[JsonConverter(typeof(PushTokenJsonConverter))]
public readonly record struct PushToken
{
    public string Value { get; }

    public PushToken(string value)
    {
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }

    public static explicit operator PushToken(string value) => new(value);

    public override string ToString() => SecretRedaction.Redact(Value);
}

internal sealed class PushTokenJsonConverter : StringBackedJsonConverter<PushToken>
{
    protected override PushToken Create(string value) => new(value);
    protected override string GetValue(PushToken value) => value.Value;
}

/// <summary>
/// Caller-supplied third-party API token (SimplyPlural or PluralKit) used by the async import
/// pipeline. Carried on <c>ImportSpCommand</c>/<c>ImportPkCommand</c> whose persisted payload
/// hashes must not move — the converter emits the raw string. <c>ToString()</c> redacts.
/// </summary>
[JsonConverter(typeof(ImportTokenJsonConverter))]
public readonly record struct ImportToken
{
    public string Value { get; }

    public ImportToken(string value)
    {
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }

    public static explicit operator ImportToken(string value) => new(value);

    public override string ToString() => SecretRedaction.Redact(Value);
}

internal sealed class ImportTokenJsonConverter : StringBackedJsonConverter<ImportToken>
{
    protected override ImportToken Create(string value) => new(value);
    protected override string GetValue(ImportToken value) => value.Value;
}

/// <summary>
/// Plaintext encryption recovery code (post-decryption). Carried on <c>ImportSpCommand</c>
/// (persisted payload — raw-string converter keeps hashes valid) and the import job queue.
/// <c>ToString()</c> redacts.
/// </summary>
[JsonConverter(typeof(RecoveryCodeJsonConverter))]
public readonly record struct RecoveryCode
{
    public string Value { get; }

    public RecoveryCode(string value)
    {
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }

    public static explicit operator RecoveryCode(string value) => new(value);

    public override string ToString() => SecretRedaction.Redact(Value);
}

internal sealed class RecoveryCodeJsonConverter : StringBackedJsonConverter<RecoveryCode>
{
    protected override RecoveryCode Create(string value) => new(value);
    protected override string GetValue(RecoveryCode value) => value.Value;
}
