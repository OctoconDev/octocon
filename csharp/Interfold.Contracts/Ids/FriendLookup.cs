using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Interfold.Contracts.Ids;

/// <summary>
/// Which registry lane a client-supplied friend-request handle routes to. Every
/// dispatch site (the friendship <c>ResolveUserId</c> paths and the controller
/// self-check) switches on this enum instead of re-inspecting the raw string.
/// </summary>
public enum FriendLookupKind
{
    /// <summary>
    /// Bare 7-char system id (<c>"abcdefg"</c>) or explicit <c>"id:"</c> prefix; routes
    /// to <c>user_registry.user_id</c>.
    /// </summary>
    Id,

    /// <summary>Username lookup, e.g. <c>"username:alice"</c>; routes to the
    /// per-region <c>users_by_username</c> fanout.</summary>
    Username,
}

/// <summary>
/// The friend-request target handle: a strict parse of the client-supplied route
/// value into either a system id (bare or <c>"id:"</c>-prefixed) or a username
/// (<c>"username:..."</c>-prefixed). Every other shape — Discord snowflakes, scoped
/// region ids (<c>"nam:..."</c>), unknown non-region prefixes, blank/half inputs —
/// fails <see cref="TryParse"/> and surfaces as a 400 at ASP.NET route binding.
///
/// <para>
/// The name is deliberately purpose-shaped (<c>FriendLookup</c>) rather than
/// shape-enumerated (<c>UsernameOrSystemId</c>): adding a future lookup shape
/// (<c>email:</c>, <c>phone:</c>, etc.) is an enum-value + parser-branch addition,
/// not another rename.
/// </para>
///
/// <para>
/// <b>Wire compatibility.</b> JSON serializes as <see cref="OriginalValue"/> — the
/// raw wire form the client sent (<c>"abcdefg"</c> / <c>"id:abcdefg"</c> /
/// <c>"username:alice"</c>). The persisted <c>SendFriendRequestCommand</c> payload
/// SHA-256 idempotency hash stays byte-identical to the pre-merge shape for every
/// surviving input. Route binding via <see cref="IParsable{TSelf}"/> — the ASP.NET
/// pipeline returns 400 automatically when <see cref="TryParse"/> returns false.
/// </para>
///
/// <para>
/// <b>Not to be confused with</b> <see cref="SystemId"/> (a post-resolution
/// principal id) or <see cref="ScopedSystemId"/> (the scoped
/// <c>{region}:{rawId}</c> composite). This type carries the pre-resolution
/// "which registry lane" fact in the type system; consumers dispatch on
/// <see cref="Kind"/> and read <see cref="Value"/> (the after-prefix content) to
/// build the actual registry query.
/// </para>
/// </summary>
[JsonConverter(typeof(FriendLookupJsonConverter))]
public readonly record struct FriendLookup : IParsable<FriendLookup>
{
    /// <summary>Which registry lane this handle routes to.</summary>
    public FriendLookupKind Kind { get; }

    /// <summary>
    /// The after-prefix content — the actual lookup value. For bare-id input this
    /// equals <see cref="OriginalValue"/>; for prefixed input it is the substring
    /// after the first colon. Consumers hand this to the registry column
    /// (<c>user_registry.user_id</c> or <c>users_by_username.username</c>).
    /// </summary>
    public string Value { get; }

    /// <summary>
    /// The raw wire form the client sent, retained verbatim for JSON serialization,
    /// logging, and idempotency-hash stability. Never null / blank for a
    /// well-constructed value.
    /// </summary>
    public string OriginalValue { get; }

    private FriendLookup(FriendLookupKind kind, string value, string originalValue)
    {
        Kind = kind;
        Value = value;
        OriginalValue = originalValue;
    }

    /// <summary>
    /// Implicit widen to the raw <see cref="string"/> — returns
    /// <see cref="OriginalValue"/> so <c>string.IsNullOrWhiteSpace(handle)</c> and
    /// similar patterns keep their historical raw-wire behaviour.
    /// <br/>
    /// <b>Hazard:</b> <c>default(FriendLookup)</c> bypasses <see cref="TryParse"/>'s
    /// validation, so widening a default slot returns <see langword="null"/>.
    /// Identical hazard to reading <see cref="OriginalValue"/> on a <c>default</c>;
    /// do not widen where a default might sneak in.
    /// </summary>
    public static implicit operator string(FriendLookup value) => value.OriginalValue;

    public override string ToString() => OriginalValue;

    /// <summary>
    /// Parse a route/JSON string into a <see cref="FriendLookup"/>. Throws
    /// <see cref="FormatException"/> for null / blank / bare-half / unknown-prefix
    /// input (Discord snowflakes and region-scoped ids fall into "unknown prefix"
    /// under the strict Id/Username contract). Prefer <see cref="TryParse"/> at the
    /// wire boundary and let ASP.NET's IParsable pipeline surface 400s automatically.
    /// </summary>
    public static FriendLookup Parse(string s, IFormatProvider? provider)
        => TryParse(s, provider, out var result)
            ? result
            : throw new FormatException($"'{s}' is not a valid FriendLookup (expected a bare id, 'id:<rawId>', or 'username:<username>').");

    /// <summary>
    /// Attempt to parse a route/JSON string. Returns <see langword="false"/> (never
    /// throws) for null / whitespace-only / <c>":foo"</c> / <c>"foo:"</c> /
    /// <c>"discord:..."</c> / region-prefixed / unknown-non-region-prefixed inputs.
    /// Accepts (case-insensitive on the prefix): bare <c>"abcdefg"</c> →
    /// <see cref="FriendLookupKind.Id"/>; <c>"id:xxx"</c> →
    /// <see cref="FriendLookupKind.Id"/>; <c>"username:xxx"</c> →
    /// <see cref="FriendLookupKind.Username"/>.
    /// </summary>
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
            // No colon → bare 7-char system id shape.
            result = new FriendLookup(FriendLookupKind.Id, s, s);
            return true;
        }

        // Colon present but nothing before or after → never dispatch a
        // partially-formed handle to a registry column.
        if (separator == 0 || separator >= s.Length - 1)
        {
            return false;
        }

        var prefix = s[..separator];
        var afterColon = s[(separator + 1)..];

        // Discriminator prefixes are lowercase-canonical on the wire; normalise for
        // a case-insensitive client while still rejecting anything not on the
        // allow-list. Region tags, Discord, and unknown non-region prefixes all
        // hit the default branch and return false — the friend-request route only
        // resolves id/username shapes; every other lookup shape belongs to the
        // OAuth callback or region-context lookups and does not flow through here.
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

/// <summary>
/// Reads the raw string via <see cref="FriendLookup.TryParse"/> and writes
/// <see cref="FriendLookup.OriginalValue"/> verbatim so persisted command payloads
/// (<c>SendFriendRequestCommand</c>) hash to the same SHA-256 as the pre-merge
/// <c>UsernameOrSystemId</c> shape for every surviving input. Invalid JSON strings
/// throw <see cref="JsonException"/> — the client contract at the wire boundary is
/// strict: the friend-request target must be id or username.
/// </summary>
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
