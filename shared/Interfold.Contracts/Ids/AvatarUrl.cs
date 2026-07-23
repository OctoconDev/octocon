using System.Text.Json;
using System.Text.Json.Serialization;

namespace Interfold.Contracts.Ids;

/// <summary>
/// An avatar (or extra-image) URL. Carries either a server-relative path (when the paired
/// <c>AvatarSource</c> is <c>Local</c> — qualified to an absolute URL at the API boundary by
/// <c>AvatarUrlQualifier</c>) or an absolute external URL. Construction does not validate:
/// legacy rows and passthrough imports carry arbitrary spellings. JSON serializes as the raw
/// string, so read models, socket frames, and hashed command payloads are unchanged.
/// </summary>
[JsonConverter(typeof(AvatarUrlJsonConverter))]
public readonly record struct AvatarUrl
{
    public string Value { get; }

    public AvatarUrl(string value)
    {
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }

    public static explicit operator AvatarUrl(string value) => new(value);

    public static implicit operator string(AvatarUrl value) => value.Value;

    public override string ToString() => Value;

    public static AvatarUrl? FromNullable(string? value) => value is null ? null : new AvatarUrl(value);
}

internal sealed class AvatarUrlJsonConverter : StringBackedJsonConverter<AvatarUrl>
{
    protected override AvatarUrl Create(string value) => new(value);
    protected override string GetValue(AvatarUrl value) => value.Value;
}
