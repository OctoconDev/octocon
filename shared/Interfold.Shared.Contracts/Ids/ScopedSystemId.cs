using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using Interfold.Shared.Contracts.Enums;

namespace Interfold.Shared.Contracts.Ids;

/// <summary>A <see cref="SystemId"/> guaranteed to be in the scoped <c>{region}:{rawId}</c>
/// wire form. Compose is idempotent (existing prefix stripped before re-applying), so
/// <c>"nam:nam:abcdefg"</c> is unconstructible through this type.</summary>
[JsonConverter(typeof(ScopedSystemIdJsonConverter))]
public readonly record struct ScopedSystemId : IParsable<ScopedSystemId>
{
    /// <summary>Canonical wire form, e.g. <c>"nam:abcdefg"</c>.</summary>
    public string Value { get; }

    /// <summary>Bare id with region prefix stripped, e.g. <c>"abcdefg"</c>.</summary>
    public string RawId { get; }

    /// <summary>Home region — matches the prefix on <see cref="Value"/>.</summary>
    public ScyllaKeyspace Region { get; }

    private ScopedSystemId(string value, string rawId, ScyllaKeyspace region)
    {
        Value = value;
        RawId = rawId;
        Region = region;
    }

    /// <summary>Compose from a resolved region and possibly-scoped id string. Any existing
    /// region prefix is stripped first (Compose is idempotent).</summary>
    /// <exception cref="ArgumentNullException"><paramref name="maybeScoped"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="maybeScoped"/> is blank after strip.</exception>
    public static ScopedSystemId Compose(ScyllaKeyspace region, string maybeScoped)
    {
        ArgumentNullException.ThrowIfNull(maybeScoped);
        var raw = StripRegionPrefix(maybeScoped);
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new ArgumentException("Cannot compose a ScopedSystemId from a blank id.", nameof(maybeScoped));
        }

        var regionWire = region.ToWire();
        return new ScopedSystemId($"{regionWire}:{raw}", raw, region);
    }

    /// <summary>Compose from a resolved region and typed <see cref="SystemId"/>.</summary>
    public static ScopedSystemId Compose(ScyllaKeyspace region, SystemId systemId)
        => Compose(region, systemId.Value);

    /// <summary>Compose from a wire-form region tag. Unknown tags throw.</summary>
    /// <exception cref="ArgumentException"><paramref name="regionWire"/> is not a recognised region tag.</exception>
    public static ScopedSystemId Compose(string regionWire, string maybeScoped)
    {
        if (!TryParseRegion(regionWire, out var region))
        {
            throw new ArgumentException(
                $"'{regionWire}' is not a recognised region wire tag (expected one of nam, eur, sam, sas, eas, ocn, gdpr).",
                nameof(regionWire));
        }
        return Compose(region, maybeScoped);
    }

    /// <summary>Strict parse for trust boundaries (JWT <c>sub</c>, incoming events).</summary>
    /// <exception cref="ArgumentException">Input is unscoped, blank, or the region tag is unknown.</exception>
    public static ScopedSystemId ParseScoped(string wire)
    {
        if (!TryParseScoped(wire, out var result))
        {
            throw new ArgumentException(
                $"'{wire}' is not a valid scoped system id (expected '<region>:<rawId>' with region in {{nam, eur, sam, sas, eas, ocn, gdpr}}).",
                nameof(wire));
        }
        return result;
    }

    /// <summary>Tolerant parse: returns <c>false</c> (never throws) for anything invalid.
    /// Used by read-side callers tolerating legacy shapes.</summary>
    public static bool TryParseScoped([NotNullWhen(true)] string? wire, out ScopedSystemId result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(wire))
        {
            return false;
        }

        var separator = wire.IndexOf(':');
        if (separator <= 0 || separator >= wire.Length - 1)
        {
            return false;
        }

        var regionRaw = wire[..separator];
        var raw = wire[(separator + 1)..];
        if (!TryParseRegion(regionRaw, out var region))
        {
            return false;
        }

        // Canonicalise casing — ScyllaKeyspace wire values are lowercase and every
        // read-side caller compares against that.
        result = new ScopedSystemId($"{region.ToWire()}:{raw}", raw, region);
        return true;
    }

    /// <summary>Return this scoped id as an unmarked <see cref="SystemId"/>.</summary>
    public SystemId AsSystemId() => new(Value);

    /// <summary>Semantic "same user" test — correct primitive for controller self-request /
    /// self-friendship guards. Canonicalises candidate so raw and same-region-scoped both
    /// self-reject; cross-region candidates compare scoped-to-scoped byte-identical.</summary>
    public bool RepresentsSameUserAs(SystemId candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate.Value))
        {
            return false;
        }

        if (TryParseScoped(candidate.Value, out var scopedCandidate))
        {
            return string.Equals(Value, scopedCandidate.Value, StringComparison.Ordinal);
        }

        return string.Equals(RawId, candidate.Value, StringComparison.Ordinal);
    }

    // Byte-identical implicit widen so persistence adapters can keep declaring SystemId
    // parameters while ingress tightens to ScopedSystemId.
    public static implicit operator SystemId(ScopedSystemId scoped) => scoped.AsSystemId();

    public override string ToString() => Value;

    // IParsable — [FromRoute] binding uses strict-parse; unscoped path segments 400.
    public static ScopedSystemId Parse(string s, IFormatProvider? provider) => ParseScoped(s);

    public static bool TryParse(
        [NotNullWhen(true)] string? s,
        IFormatProvider? provider,
        [MaybeNullWhen(false)] out ScopedSystemId result)
        => TryParseScoped(s, out result);

    public static string StripRegionPrefix(SystemId systemId) => StripRegionPrefix(systemId.Value);

    /// <summary>Strip a leading recognised <c>{region}:</c> prefix once; unrecognised prefixes
    /// (e.g. <c>"username:"</c>) return unchanged. Single-pass: a double-prefixed
    /// <c>"nam:nam:abcdefg"</c> yields <c>"nam:abcdefg"</c>.</summary>
    public static string StripRegionPrefix(string maybeScoped)
    {
        if (string.IsNullOrWhiteSpace(maybeScoped))
        {
            return maybeScoped;
        }

        var separator = maybeScoped.IndexOf(':');
        if (separator <= 0)
        {
            return maybeScoped;
        }

        if (!TryParseRegion(maybeScoped[..separator], out _))
        {
            return maybeScoped;
        }

        return maybeScoped[(separator + 1)..];
    }

    // Case-insensitive region-tag lookup; differs from ParseScyllaKeyspace by dropping the
    // "blank defaults to Nam" fallback (blank here means "no prefix at all").
    internal static bool TryParseRegion(string raw, out ScyllaKeyspace region)
        => raw.TryParseWire(out region);
}

// Emits and reads the raw ScopedSystemId.Value string — byte-identical to
// SystemIdJsonConverter so a declared-type migration is zero wire change.
internal sealed class ScopedSystemIdJsonConverter : JsonConverter<ScopedSystemId>
{
    public override ScopedSystemId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var raw = reader.GetString();
        return ScopedSystemId.ParseScoped(raw ?? string.Empty);
    }

    public override void Write(Utf8JsonWriter writer, ScopedSystemId value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value);
}
