using System.Text.Json.Serialization;

namespace Interfold.Shared.Contracts.Ids;

/// <summary>
/// One-time account-link token minted by <c>GET /settings/link_token</c> and consumed by the
/// <c>/auth/link/{provider}</c> flow. JSON serializes as the raw string
/// (<c>LinkTokenReadModel.Token</c> response body is unchanged); <c>ToString()</c> redacts.
/// </summary>
[JsonConverter(typeof(LinkTokenJsonConverter))]
public readonly record struct LinkToken
{
    public string Value { get; }

    public LinkToken(string value)
    {
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }

    public static explicit operator LinkToken(string value) => new(value);

    public override string ToString() => SecretRedaction.Redact(Value);

    /// <summary>Total function: null/empty/whitespace input → null wrapper.</summary>
    public static LinkToken? From(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : new LinkToken(value);
}

internal sealed class LinkTokenJsonConverter : StringBackedJsonConverter<LinkToken>
{
    protected override LinkToken Create(string value) => new(value);
    protected override string GetValue(LinkToken value) => value.Value;
}
