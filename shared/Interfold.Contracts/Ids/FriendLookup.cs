using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Interfold.Contracts.Ids;

/// <summary>Which registry lane a client-supplied friend-request handle routes to.</summary>
public enum FriendLookupKind
{
    /// <summary>Bare 7-char system id or <c>"id:"</c>-prefixed; → <c>user_registry.user_id</c>.</summary>
    Id,

    /// <summary>Username lookup (<c>"username:alice"</c>); → per-region <c>users_by_username</c>.</summary>
    Username,
}

/// <summary>Strictly-parsed friend-request target: either a bare/prefixed system id or a
/// <c>"username:..."</c>-prefixed username. Discord snowflakes, scoped region ids, unknown
/// prefixes, and blank/half inputs fail <see cref="TryParse"/> and surface as 400 at route
/// binding. JSON emits <see cref="OriginalValue"/> verbatim so persisted command payload
/// hashes stay stable.</summary>
[JsonConverter(typeof(FriendLookupJsonConverter))]
public readonly record struct FriendLookup : IParsable<FriendLookup>
{
    public FriendLookupKind Kind { get; }

    /// <summary>After-prefix content — the value handed to the registry column.</summary>
    public string Value { get; }

    /// <summary>Raw wire form retained verbatim for JSON, logging, and hash stability.</summary>
    public string OriginalValue { get; }

    private FriendLookup(FriendLookupKind kind, string value, string originalValue)
    {
        Kind = kind;
        Value = value;
        OriginalValue = originalValue;
    }

    // Widens to OriginalValue so IsNullOrWhiteSpace-style checks keep raw-wire semantics.
    // Hazard: default(FriendLookup).OriginalValue is null; don't widen a default slot.
    public static implicit operator string(FriendLookup value) => value.OriginalValue;

    public override string ToString() => OriginalValue;

    public static FriendLookup Parse(string s, IFormatProvider? provider)
        => TryParse(s, provider, out var result)
            ? result
            : throw new FormatException($"'{s}' is not a valid FriendLookup (expected a bare id, 'id:<rawId>', or 'username:<username>').");

    public static bool TryParse(
        [NotNullWhen(true)] string? s,
        IFormatProvider? provider,
        [MaybeNullWhen(false)] out FriendLookup result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(s))
        {
            return false;
        }

        var separator = s.IndexOf(':');
        if (separator < 0)
        {
            result = new FriendLookup(FriendLookupKind.Id, s, s);
            return true;
        }

        if (separator == 0 || separator >= s.Length - 1)
        {
            return false;
        }

        var prefix = s[..separator];
        var afterColon = s[(separator + 1)..];

        // Wire prefixes are lowercase-canonical; other lookup shapes (Discord, region tags)
        // belong to different routes and fall through to false here.
        switch (prefix.ToLowerInvariant())
        {
            case "id":
                result = new FriendLookup(FriendLookupKind.Id, afterColon, s);
                return true;
            case "username":
                result = new FriendLookup(FriendLookupKind.Username, afterColon, s);
                return true;
            default:
                return false;
        }
    }
}

internal sealed class FriendLookupJsonConverter : JsonConverter<FriendLookup>
{
    public override FriendLookup Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var raw = reader.GetString();
        return FriendLookup.TryParse(raw, provider: null, out var result)
            ? result
            : throw new JsonException($"'{raw}' is not a valid FriendLookup JSON value (expected a bare id, 'id:<rawId>', or 'username:<username>').");
    }

    public override void Write(Utf8JsonWriter writer, FriendLookup value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.OriginalValue);
}
