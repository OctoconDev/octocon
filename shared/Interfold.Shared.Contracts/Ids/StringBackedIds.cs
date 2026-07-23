using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Interfold.Shared.Contracts.Ids;

// Guid-backed entity IDs: readonly record struct wrapping Guid, JSON emits the compact
// 32-char lowercase hex form (Guid.ToString("N")). Read accepts both "N" and hyphenated
// via UuidString.TryParse.

// PollId + PollIdJsonConverter live in Interfold.Polls.Contracts/Ids/PollId.cs.
// EntryId + EntryIdJsonConverter live in Interfold.Journals.Contracts/Ids/EntryId.cs.
// TagId + TagIdJsonConverter live in Interfold.Tags.Contracts/Ids/TagId.cs.
// FrontId + FrontIdJsonConverter live in Interfold.Fronting.Contracts/Ids/FrontId.cs.

/// <summary>Strongly-typed wrapper around a settings-field id (Guid, wire form is 32-char lowercase hex).</summary>
[JsonConverter(typeof(FieldIdJsonConverter))]
public readonly record struct FieldId(Guid Value) : IParsable<FieldId>
{
    public static readonly FieldId Empty = new(Guid.Empty);

    public static explicit operator FieldId(Guid value) => new(value);
    public static implicit operator Guid(FieldId value) => value.Value;

    public override string ToString() => Value.ToString("N");

    public static FieldId Parse(string s, IFormatProvider? provider)
        => UuidString.TryParse(s, out var g)
            ? new(g)
            : throw new FormatException($"Value '{s}' is not a valid FieldId.");

    public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, [MaybeNullWhen(false)] out FieldId result)
    {
        if (s is null || !UuidString.TryParse(s, out var g)) { result = default; return false; }
        result = new FieldId(g);
        return true;
    }
}

// Common JSON converter shape for the Guid-backed IDs above. Writes 32-char lowercase
// hex; reads accept both "N" and hyphenated forms. Concrete stubs are named individually
// because [JsonConverter(typeof(...))] requires a concrete class name. Public so
// per-feature Contracts projects can derive their own concrete stubs.
public abstract class GuidIdJsonConverter<T> : JsonConverter<T>
{
    protected abstract T Create(Guid value);
    protected abstract Guid GetValue(T value);
    protected abstract string TypeLabel { get; }

    public sealed override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => UuidString.TryParse(reader.GetString() ?? string.Empty, out var g)
            ? Create(g)
            : throw new JsonException($"{TypeLabel} JSON value must be a Guid string (compact or hyphenated).");

    public sealed override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        => writer.WriteStringValue(GetValue(value).ToString("N"));
}

internal sealed class FieldIdJsonConverter : GuidIdJsonConverter<FieldId>
{
    protected override FieldId Create(Guid value) => new(value);
    protected override Guid GetValue(FieldId value) => value.Value;
    protected override string TypeLabel => nameof(FieldId);
}
