using System.Text.Json;
using System.Text.Json.Serialization;

namespace Interfold.Contracts;

/// <summary>
/// Link-presence flag for the socket self payload. The legacy wire contract (inherited from
/// the Elixir server) reuses the identity-shaped member names (<c>discord_id</c>,
/// <c>apple_id</c>, <c>email</c>, ...) but carries only <c>"SET"</c> or null — the Kotlin
/// client null-checks the fields to drive the account-linking settings UI and never reads
/// the value. This type makes that explicit instead of posing as an identity string.
/// </summary>
[JsonConverter(typeof(AccountLinkFlagJsonConverter))]
public readonly record struct AccountLinkFlag(bool IsLinked)
{
    public const string SetWireValue = "SET";

    public static readonly AccountLinkFlag NotLinked = new(false);

    /// <summary>Linked when the underlying identity value is present; the value itself never
    /// leaves the server.</summary>
    public static AccountLinkFlag FromValuePresence(string? identityValue)
        => new(!string.IsNullOrWhiteSpace(identityValue));
}

internal sealed class AccountLinkFlagJsonConverter : JsonConverter<AccountLinkFlag>
{
    public override bool HandleNull => true;

    public override AccountLinkFlag Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => new(reader.TokenType == JsonTokenType.String && !string.IsNullOrWhiteSpace(reader.GetString()));

    public override void Write(Utf8JsonWriter writer, AccountLinkFlag value, JsonSerializerOptions options)
    {
        if (value.IsLinked)
        {
            writer.WriteStringValue(AccountLinkFlag.SetWireValue);
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}
