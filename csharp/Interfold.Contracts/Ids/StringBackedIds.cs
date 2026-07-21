using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Interfold.Contracts.Ids;

// The Guid-backed entity IDs share the same shape. Each is a `readonly record struct`
// wrapping a Guid, backed by a JsonConverter that emits the compact 32-char lowercase
// hex form (Guid.ToString("N")). Wire byte-identical to the previous string-backed
// spelling, so idempotency hashes and HTTP client contracts stay stable while the
// per-repository `TryParseUuid` guards go away — validation happens once, at the
// boundary (JsonConverter.Read and IParsable.TryParse route through
// UuidString.TryParse which accepts both "N" and hyphenated Guid forms).

/// <summary>Strongly-typed wrapper around a tag id (Guid, wire form is 32-char lowercase hex).</summary>
[JsonConverter(typeof(TagIdJsonConverter))]
public readonly record struct TagId(Guid Value) : IParsable<TagId>
{
    /// <summary>
    /// The zero-Guid tag id — call sites can write <c>tagId == TagId.Empty</c> instead of
    /// reaching through <c>.Value</c> to compare against <see cref="Guid.Empty"/>. Mirrors
    /// <see cref="Guid.Empty"/> so the intent-checking idiom stays visible even after the
    /// wrapper hides the underlying <see cref="Guid"/>.
    /// </summary>
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

/// <summary>Strongly-typed wrapper around a poll id (Guid, wire form is 32-char lowercase hex).</summary>
[JsonConverter(typeof(PollIdJsonConverter))]
public readonly record struct PollId(Guid Value) : IParsable<PollId>
{
    /// <summary>
    /// The zero-Guid poll id. See <see cref="TagId.Empty"/> for the general story.
    /// </summary>
    public static readonly PollId Empty = new(Guid.Empty);

    public static explicit operator PollId(Guid value) => new(value);
    public static implicit operator Guid(PollId value) => value.Value;

    public override string ToString() => Value.ToString("N");

    public static PollId Parse(string s, IFormatProvider? provider)
        => UuidString.TryParse(s, out var g)
            ? new(g)
            : throw new FormatException($"Value '{s}' is not a valid PollId.");

    public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, [MaybeNullWhen(false)] out PollId result)
    {
        if (s is null || !UuidString.TryParse(s, out var g)) { result = default; return false; }
        result = new PollId(g);
        return true;
    }
}

/// <summary>Strongly-typed wrapper around a journal entry id (Guid, wire form is 32-char lowercase hex).</summary>
[JsonConverter(typeof(EntryIdJsonConverter))]
public readonly record struct EntryId(Guid Value) : IParsable<EntryId>
{
    /// <summary>
    /// The zero-Guid entry id. See <see cref="TagId.Empty"/> for the general story.
    /// </summary>
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

/// <summary>Strongly-typed wrapper around a front id (Guid, wire form is 32-char lowercase hex).</summary>
[JsonConverter(typeof(FrontIdJsonConverter))]
public readonly record struct FrontId(Guid Value) : IParsable<FrontId>
{
    /// <summary>
    /// The zero-Guid front id. See <see cref="TagId.Empty"/> for the general story.
    /// </summary>
    public static readonly FrontId Empty = new(Guid.Empty);

    public static explicit operator FrontId(Guid value) => new(value);
    public static implicit operator Guid(FrontId value) => value.Value;

    public override string ToString() => Value.ToString("N");

    public static FrontId Parse(string s, IFormatProvider? provider)
        => UuidString.TryParse(s, out var g)
            ? new(g)
            : throw new FormatException($"Value '{s}' is not a valid FrontId.");

    public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, [MaybeNullWhen(false)] out FrontId result)
    {
        if (s is null || !UuidString.TryParse(s, out var g)) { result = default; return false; }
        result = new FrontId(g);
        return true;
    }

    public const int MaxCommentLength = 50;

    public static bool IsValidComment(string? comment) => (comment?.Length ?? 0) <= MaxCommentLength;
}

/// <summary>Strongly-typed wrapper around a settings-field id (Guid, wire form is 32-char lowercase hex).</summary>
[JsonConverter(typeof(FieldIdJsonConverter))]
public readonly record struct FieldId(Guid Value) : IParsable<FieldId>
{
    /// <summary>
    /// The zero-Guid field id. See <see cref="TagId.Empty"/> for the general story.
    /// </summary>
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

/// <summary>
/// Common JSON converter shape for the Guid-backed entity IDs above. Preserves the
/// historic wire form (compact 32-char lowercase hex, <c>Guid.ToString("N")</c>) so
/// persisted command payloads keep the same SHA-256 hash for idempotency replay and
/// existing HTTP clients see byte-identical response bodies. Read accepts both "N"
/// and hyphenated forms (<see cref="UuidString.TryParse"/>) so legacy persisted
/// payloads deserialize. Concrete stubs stay <c>sealed</c> and are named individually
/// because <c>[JsonConverter(typeof(...))]</c> requires a concrete class name.
/// </summary>
internal abstract class GuidIdJsonConverter<T> : JsonConverter<T>
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

internal sealed class TagIdJsonConverter : GuidIdJsonConverter<TagId>
{
    protected override TagId Create(Guid value) => new(value);
    protected override Guid GetValue(TagId value) => value.Value;
    protected override string TypeLabel => nameof(TagId);
}

internal sealed class PollIdJsonConverter : GuidIdJsonConverter<PollId>
{
    protected override PollId Create(Guid value) => new(value);
    protected override Guid GetValue(PollId value) => value.Value;
    protected override string TypeLabel => nameof(PollId);
}

internal sealed class EntryIdJsonConverter : GuidIdJsonConverter<EntryId>
{
    protected override EntryId Create(Guid value) => new(value);
    protected override Guid GetValue(EntryId value) => value.Value;
    protected override string TypeLabel => nameof(EntryId);
}

internal sealed class FrontIdJsonConverter : GuidIdJsonConverter<FrontId>
{
    protected override FrontId Create(Guid value) => new(value);
    protected override Guid GetValue(FrontId value) => value.Value;
    protected override string TypeLabel => nameof(FrontId);
}

internal sealed class FieldIdJsonConverter : GuidIdJsonConverter<FieldId>
{
    protected override FieldId Create(Guid value) => new(value);
    protected override Guid GetValue(FieldId value) => value.Value;
    protected override string TypeLabel => nameof(FieldId);
}
