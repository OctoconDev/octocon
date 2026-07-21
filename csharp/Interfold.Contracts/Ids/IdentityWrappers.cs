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

    /// <summary>
    /// Explicit narrow so call sites can write <c>(Username)raw</c> instead of
    /// <c>new Username(raw)</c>. See <see cref="SystemId"/> for the wider rationale.
    /// </summary>
    public static explicit operator Username(string value) => new(value);

    /// <summary>
    /// Implicit widen to the raw <see cref="string"/>. Safe here because the value is public
    /// (unlike the PII wrappers below, which deliberately do not offer a widen).
    /// </summary>
    public static implicit operator string(Username value) => value.Value;

    public override string ToString() => Value;
}

internal sealed class UsernameJsonConverter : JsonConverter<Username>
{
    // Client bodies bind Username at non-nullable positions (SettingsUsernameRequest);
    // JSON null maps to an empty value so the command handler's username_invalid
    // rejection fires exactly as it did when the property was a nullable string.
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

    /// <summary>
    /// Explicit narrow so call sites can write <c>(DiscordId)raw</c> instead of
    /// <c>new DiscordId(raw)</c>.
    ///
    /// <para>
    /// <b>No implicit widen by design.</b> <c>ToString()</c> redacts (see
    /// <see cref="SecretRedaction"/>); an <c>implicit operator string</c> would silently defeat
    /// that redaction under any <c>Foo(string)</c> overload. Use <see cref="Value"/> at the
    /// persistence / DB / HTTP boundary — the explicit unwrap is greppable and audit-friendly.
    /// </para>
    /// </summary>
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

    /// <summary>
    /// Explicit narrow so call sites can write <c>(Email)raw</c> instead of
    /// <c>new Email(raw)</c>. No implicit widen by design — see <see cref="DiscordId"/>'s
    /// operator xml-doc for the redaction-preservation rationale.
    /// </summary>
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

    /// <summary>
    /// Explicit narrow so call sites can write <c>(AppleId)raw</c> instead of
    /// <c>new AppleId(raw)</c>. No implicit widen by design — see <see cref="DiscordId"/>'s
    /// operator xml-doc for the redaction-preservation rationale.
    /// </summary>
    public static explicit operator AppleId(string value) => new(value);

    public override string ToString() => SecretRedaction.Redact(Value);
}

internal sealed class AppleIdJsonConverter : StringBackedJsonConverter<AppleId>
{
    protected override AppleId Create(string value) => new(value);
    protected override string GetValue(AppleId value) => value.Value;
}
