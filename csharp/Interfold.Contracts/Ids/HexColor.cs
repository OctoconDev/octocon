using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Interfold.Contracts.Ids;

/// <summary>
/// A display color for alters, tags, and journals — canonically a <c>#RRGGBB</c> hex string
/// (also accepts <c>#RGB</c> and <c>#RRGGBBAA</c> per <see cref="IsWellFormedString"/>).
///
/// <para>
/// <b>Fully strict contract.</b> Every construction path validates. The constructor,
/// <see cref="Parse(string, IFormatProvider?)"/>, <see cref="TryParse(string?, IFormatProvider?, out HexColor)"/>,
/// the JSON reader, and <see cref="FromNullable(string?)"/> all reject non-well-formed
/// values. This is safe because:
/// </para>
/// <list type="bullet">
///   <item><description>The first-party client (<c>octocon-app</c>) validates against
///   <c>Regex("^#[0-9A-Fa-f]{6}$")</c> before every write, so every color it sends is a
///   strict subset of what this type accepts.</description></item>
///   <item><description>The Simply Plural importer normalises bare 6-char hex to
///   <c>#RRGGBB</c> (and rejects everything else) inside <c>ParseColor</c> before ever
///   constructing a <see cref="HexColor"/>.</description></item>
///   <item><description>Idempotency-replay does not carry colors — the stored
///   <c>outcome_payload</c> in <c>octocon_idempotency</c> is always an id-only
///   <c>ICommandResult</c> shape (<c>TagCommandResult</c>, <c>AlterCommandResult</c>,
///   etc.), never a payload with a color.</description></item>
///   <item><description>Historical malformed rows are normalised (or nulled) by the
///   one-shot <c>HexColorFixupService</c> before the strict contract is enforced at
///   the read boundary.</description></item>
/// </list>
///
/// <para>
/// JSON serializes as the raw string. Model binding surfaces malformed inbound values as
/// a 400 (the JSON reader throws <see cref="JsonException"/>, matching the house style in
/// <see cref="StringBackedIds"/>' Guid-backed id converters).
/// </para>
/// </summary>
[JsonConverter(typeof(HexColorJsonConverter))]
public readonly record struct HexColor : IParsable<HexColor>
{
    public string Value { get; }

    /// <summary>
    /// Validates <paramref name="value"/> against <see cref="IsWellFormedString"/> and
    /// throws <see cref="FormatException"/> when it is not well-formed. Every wire-side
    /// construction (the explicit narrow, the JSON reader, direct <c>new</c>) routes
    /// through this ctor so malformed input can never live inside a <see cref="HexColor"/>.
    /// </summary>
    public HexColor(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!IsWellFormedString(value))
        {
            throw new FormatException(
                $"'{value}' is not a well-formed HexColor (expected '#RGB', '#RRGGBB', or '#RRGGBBAA').");
        }

        Value = value;
    }

    /// <summary>
    /// Explicit narrow so call sites can write <c>(HexColor)raw</c> instead of
    /// <c>new HexColor(raw)</c>. Inherits the constructor's strict validation.
    /// See <see cref="SystemId"/> for the wider rationale on the narrow/widen shape.
    /// </summary>
    public static explicit operator HexColor(string value) => new(value);

    /// <summary>
    /// Implicit widen to the raw <see cref="string"/> for wire / DB boundary use.
    /// </summary>
    public static implicit operator string(HexColor value) => value.Value;

    /// <summary>
    /// True when <see cref="Value"/> matches <c>#RGB</c>, <c>#RRGGBB</c>, or
    /// <c>#RRGGBBAA</c>. A tautology for values constructed through the public API
    /// (which enforce the same predicate at construction time), but retained for
    /// defence-in-depth checks and for explicit assertions in tests.
    /// </summary>
    public bool IsWellFormed => IsWellFormedString(Value);

    public override string ToString() => Value;

    /// <summary>
    /// Parses a well-formed hex color. Throws <see cref="FormatException"/> for
    /// <see langword="null"/>, empty, or non-well-formed input. Enables <c>[FromRoute]</c> /
    /// <c>[FromQuery]</c> binding via <see cref="IParsable{TSelf}"/>.
    /// </summary>
    public static HexColor Parse(string s, IFormatProvider? provider)
    {
        ArgumentNullException.ThrowIfNull(s);
        return new HexColor(s);
    }

    /// <summary>
    /// Tries to parse a well-formed hex color. Returns <see langword="false"/> (never throws)
    /// for <see langword="null"/>, empty, or non-well-formed input.
    /// </summary>
    public static bool TryParse(
        [NotNullWhen(true)] string? s,
        IFormatProvider? provider,
        [MaybeNullWhen(false)] out HexColor result)
    {
        if (s is null || !IsWellFormedString(s))
        {
            result = default;
            return false;
        }

        result = new HexColor(s);
        return true;
    }

    /// <summary>
    /// Persistence-boundary rehydration: <see langword="null"/> in yields
    /// <see langword="null"/> out; a well-formed string yields a
    /// <see cref="HexColor"/>; a malformed string throws <see cref="FormatException"/>.
    /// The historical "tolerate anything from the DB" shape is gone — the
    /// <c>HexColorFixupService</c> guarantees every persisted color is well-formed
    /// before the strict boundary is enforced.
    /// </summary>
    public static HexColor? FromNullable(string? value) => value is null ? null : new HexColor(value);

    /// <summary>
    /// Recover a canonical hex-color spelling from a possibly-noisy raw string.
    /// Returns <see langword="null"/> for null / whitespace / unrecoverable input;
    /// returns the trimmed input verbatim when it is already well-formed; returns
    /// <c>#RRGGBB</c> when the input is bare six-hex (with or without a leading
    /// <c>#</c>) after trimming.
    ///
    /// <para>
    /// Used by the Simply Plural importer (SP historically stores both shapes
    /// interchangeably) and by the one-shot <c>HexColorFixupService</c> that
    /// normalises legacy DB rows before <see cref="FromNullable"/> starts
    /// throwing on them. Callers that receive <see langword="null"/> and had a
    /// non-null input have detected an unrecoverable value.
    /// </para>
    /// </summary>
    public static string? Normalise(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var trimmed = raw.Trim();
        if (IsWellFormedString(trimmed))
            return trimmed;

        var stripped = trimmed.StartsWith('#') ? trimmed[1..] : trimmed;
        if (stripped.Length != 6)
            return null;

        foreach (var c in stripped)
        {
            if (!Uri.IsHexDigit(c))
                return null;
        }

        return "#" + stripped;
    }

    /// <summary>
    /// Predicate implementation shared by <see cref="IsWellFormed"/>, the constructor,
    /// <see cref="TryParse"/>, and <see cref="Normalise"/>. Accepts <c>#RGB</c>,
    /// <c>#RRGGBB</c>, and <c>#RRGGBBAA</c>; case-insensitive on the hex digits.
    /// </summary>
    private static bool IsWellFormedString(ReadOnlySpan<char> value)
    {
        if (value.Length == 0 || value[0] != '#')
            return false;

        var digits = value.Length - 1;
        if (digits is not (3 or 6 or 8))
            return false;

        for (var i = 1; i < value.Length; i++)
        {
            if (!Uri.IsHexDigit(value[i]))
                return false;
        }

        return true;
    }
}

internal sealed class HexColorJsonConverter : JsonConverter<HexColor>
{
    public override HexColor Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var raw = reader.GetString();
        if (raw is null || !HexColor.TryParse(raw, provider: null, out var result))
        {
            throw new JsonException(
                $"'{raw}' is not a well-formed HexColor JSON value (expected '#RGB', '#RRGGBB', or '#RRGGBBAA').");
        }

        return result;
    }

    public override void Write(Utf8JsonWriter writer, HexColor value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value);
}
