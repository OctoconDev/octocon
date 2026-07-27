using System.Text.Json.Serialization;

namespace Interfold.Shared.Contracts.Ids;

/// <summary>
/// The JWT carried in the <c>phx_join</c> payload's <c>token</c> member (mirrors the
/// query-string token the socket upgrade authenticated with). JSON serializes as the raw
/// string; <c>ToString()</c> redacts so a logged join payload can't leak the credential.
/// </summary>
[JsonConverter(typeof(SocketTokenJsonConverter))]
public readonly record struct SocketToken
{
    public string Value { get; }

    public SocketToken(string value)
    {
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }

    public static explicit operator SocketToken(string value) => new(value);

    public override string ToString() => SecretRedaction.Redact(Value);
}

internal sealed class SocketTokenJsonConverter : StringBackedJsonConverter<SocketToken>
{
    protected override SocketToken Create(string value) => new(value);
    protected override string GetValue(SocketToken value) => value.Value;
}
