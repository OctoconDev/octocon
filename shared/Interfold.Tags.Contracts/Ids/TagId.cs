using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace Interfold.Contracts.Ids;

/// <summary>Strongly-typed wrapper around a tag id (Guid, wire form is 32-char lowercase hex).</summary>
[JsonConverter(typeof(TagIdJsonConverter))]
public readonly record struct TagId(Guid Value) : IParsable<TagId>
{
    public static readonly TagId Empty = new(Guid.Empty);

    public static explicit operator TagId(Guid value) => new(value);
    public static implicit operator Guid(TagId value) => value.Value;

    public override string ToString() => Value.ToString("N");

    public static TagId Parse(string s, IFormatProvider? provider)
        => UuidString.TryParse(s, out var g)
            ? new(g)
            : throw new FormatException($"Value '{s}' is not a valid TagId.");

    public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, [MaybeNullWhen(false)] out TagId result)
    {
        if (s is null || !UuidString.TryParse(s, out var g)) { result = default; return false; }
        result = new TagId(g);
        return true;
    }
}

internal sealed class TagIdJsonConverter : GuidIdJsonConverter<TagId>
{
    protected override TagId Create(Guid value) => new(value);
    protected override Guid GetValue(TagId value) => value.Value;
    protected override string TypeLabel => nameof(TagId);
}
