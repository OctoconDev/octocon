using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using Interfold.Shared.Contracts.Ids;

namespace Interfold.Journals.Contracts.Ids;

/// <summary>Strongly-typed wrapper around a journal entry id (Guid, wire form is 32-char lowercase hex).</summary>
[JsonConverter(typeof(EntryIdJsonConverter))]
public readonly record struct EntryId(Guid Value) : IParsable<EntryId>
{
    public static readonly EntryId Empty = new(Guid.Empty);

    public static explicit operator EntryId(Guid value) => new(value);
    public static implicit operator Guid(EntryId value) => value.Value;

    public override string ToString() => Value.ToString("N");

    public static EntryId Parse(string s, IFormatProvider? provider)
        => UuidString.TryParse(s, out var g)
            ? new(g)
            : throw new FormatException($"Value '{s}' is not a valid EntryId.");

    public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, [MaybeNullWhen(false)] out EntryId result)
    {
        if (s is null || !UuidString.TryParse(s, out var g)) { result = default; return false; }
        result = new EntryId(g);
        return true;
    }
}

internal sealed class EntryIdJsonConverter : GuidIdJsonConverter<EntryId>
{
    protected override EntryId Create(Guid value) => new(value);
    protected override Guid GetValue(EntryId value) => value.Value;
    protected override string TypeLabel => nameof(EntryId);
}
