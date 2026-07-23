using System.Text.Json;
using System.Text.Json.Serialization;

namespace Interfold.Contracts.Ids;

/// <summary>
/// A system's public display/handle name. Public value — <c>ToString()</c> returns the raw
/// string (unlike the PII wrappers below). JSON serializes as the raw string, so read models,
/// socket payloads, and the persisted <c>UpdateUsernameCommand</c> payload hashes are unchanged.
/// </summary>
[JsonConverter(typeof(UsernameJsonConverter))]
public readonly record struct Username
{
    public string Value { get; }

    public Username(string value)
    {
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }

    public static explicit operator Username(string value) => new(value);

    // Safe implicit widen — Username is public data, unlike the PII wrappers below.
    public static implicit operator string(Username value) => value.Value;

    public override string ToString() => Value;
}

internal sealed class UsernameJsonConverter : JsonConverter<Username>
{
    // JSON null → empty so the command handler's username_invalid path fires as it did
    // when the property was a nullable string.
    public override bool HandleNull => true;

    public override Username Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => new(reader.TokenType == JsonTokenType.Null ? string.Empty : reader.GetString() ?? string.Empty);

    public override void Write(Utf8JsonWriter writer, Username value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value);
}

/// <summary>
/// A linked Discord account's snowflake id. PII — <c>ToString()</c> redacts (see
/// <see cref="SecretRedaction"/>); JSON serializes as the raw string so the
/// <c>discord_account_linked</c> socket payload and friend-profile bodies are unchanged.
/// </summary>
[JsonConverter(typeof(DiscordIdJsonConverter))]
public readonly record struct DiscordId
{
    public string Value { get; }

    public DiscordId(string value)
    {
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }

    // No implicit widen by design: ToString() redacts, an implicit string operator
    // would silently defeat that under any Foo(string) overload. Use .Value at
    // persistence / DB / HTTP boundaries — explicit unwrap is greppable and audit-friendly.
    public static explicit operator DiscordId(string value) => new(value);

    public override string ToString() => SecretRedaction.Redact(Value);
}

internal sealed class DiscordIdJsonConverter : StringBackedJsonConverter<DiscordId>
{
    protected override DiscordId Create(string value) => new(value);
    protected override string GetValue(DiscordId value) => value.Value;
}

/// <summary>
/// A linked account email address (Google links resolve to email). PII — <c>ToString()</c>
/// redacts; JSON serializes as the raw string.
/// </summary>
[JsonConverter(typeof(EmailJsonConverter))]
public readonly record struct Email
{
    public string Value { get; }

    public Email(string value)
    {
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }

    public static explicit operator Email(string value) => new(value);

    public override string ToString() => SecretRedaction.Redact(Value);
}

internal sealed class EmailJsonConverter : StringBackedJsonConverter<Email>
{
    protected override Email Create(string value) => new(value);
    protected override string GetValue(Email value) => value.Value;
}

/// <summary>
/// A linked Apple account's stable subject identifier (the <c>sub</c> claim from Sign in with
/// Apple; stored in the <c>apple_id</c> column). PII — <c>ToString()</c> redacts; JSON
/// serializes as the raw string.
/// </summary>
[JsonConverter(typeof(AppleIdJsonConverter))]
public readonly record struct AppleId
{
    public string Value { get; }

    public AppleId(string value)
    {
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }

    public static explicit operator AppleId(string value) => new(value);

    public override string ToString() => SecretRedaction.Redact(Value);
}

internal sealed class AppleIdJsonConverter : StringBackedJsonConverter<AppleId>
{
    protected override AppleId Create(string value) => new(value);
    protected override string GetValue(AppleId value) => value.Value;
}
